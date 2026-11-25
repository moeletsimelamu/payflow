using PayFlow.Infrastructure.Telemetry;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Payments.Application;

/// <summary>
/// The second, atomic step of every payment state change: apply the outcome, post ledger entries,
/// enqueue the integration event and (for the create request) store the idempotent response, all in
/// one database transaction. Shared by the API, the pending resolver and crash recovery.
/// </summary>
internal sealed class PaymentProcessor(
    PayFlowDbContext db,
    ILedger ledger,
    IOutbox outbox,
    IdempotencyStore idempotency,
    ICorrelationContext correlation,
    TimeProvider clock,
    IOptions<JsonOptions> json)
{
    public const string ResourceType = "payment";

    /// <summary><see cref="AppliedHere"/> is false when another worker had already completed the payment.</summary>
    public sealed record ProcessingResult(string Body, bool AppliedHere);

    /// <summary>
    /// Processing → outcome. Returns the response body stored for idempotent replay.
    /// The request that created the payment and <see cref="ProcessingRecoveryJob"/> can race to
    /// complete the same payment; both lock the row first, and whoever comes second finds it no
    /// longer Processing and returns the outcome the first one recorded, without touching anything.
    /// </summary>
    public async Task<ProcessingResult> CompleteProcessingAsync(Payment payment, SimulatedDecision decision, Actor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAndReloadAsync(payment, ct);
        if (payment.Status != PaymentStatus.Processing)
        {
            await transaction.CommitAsync(ct);
            return new ProcessingResult(JsonSerializer.Serialize(payment.ToResponse(includeHistory: false), json.Value.SerializerOptions), AppliedHere: false);
        }

        var now = clock.GetUtcNow();
        payment.ApplyDecision(decision, now, actor, correlation.CorrelationId);
        await RecordConsequencesAsync(payment, now, ct);

        var body = JsonSerializer.Serialize(payment.ToResponse(includeHistory: false), json.Value.SerializerOptions);
        await idempotency.CompleteForResourceAsync(ResourceType, payment.Id, StatusCodes.Created, body, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        RecordOutcome(payment);
        return new ProcessingResult(body, AppliedHere: true);
    }

    /// <summary>Pending → final outcome. Returns false if another resolver got there first.</summary>
    public async Task<bool> ResolvePendingAsync(Payment payment, SimulatedDecision decision, Actor actor, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await LockAndReloadAsync(payment, ct);
        if (payment.Status != PaymentStatus.Pending)
        {
            return false;
        }

        var now = clock.GetUtcNow();
        payment.ResolvePending(decision, now, actor, correlation.CorrelationId);
        await RecordConsequencesAsync(payment, now, ct);
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        RecordOutcome(payment);
        return true;
    }

    private static void RecordOutcome(Payment payment)
    {
        var status = payment.Status.ToString();
        PayFlowTelemetry.PaymentOutcomes.Add(1, PayFlowTelemetry.Tag("payflow.payment.status", status));
        System.Diagnostics.Activity.Current?.SetTag("payflow.payment.status", status);
    }

    /// <summary>Row lock held until commit (the same lock refunds, reversals and settlement take), then a fresh read.</summary>
    private async Task LockAndReloadAsync(Payment payment, CancellationToken ct)
    {
        await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM payments.payments WHERE id = {payment.Id} FOR UPDATE").ToListAsync(ct);
        await db.Entry(payment).ReloadAsync(ct);
    }

    private async Task RecordConsequencesAsync(Payment payment, DateTimeOffset now, CancellationToken ct)
    {
        if (payment.Status == PaymentStatus.Succeeded)
        {
            await PostCaptureAsync(payment, ct);
        }

        if (PaymentMapping.EventTypeFor(payment.Status) is { } eventType)
        {
            outbox.Enqueue(eventType, payment.ToEvent(now));
        }
    }

    /// <summary>docs/PAYMENT_LIFECYCLE.md §5: gross to the merchant, then the fee to PayFlow revenue.</summary>
    private async Task PostCaptureAsync(Payment payment, CancellationToken ct)
    {
        await ledger.PostAsync(
            new JournalEntryRequest(
                JournalEntryTypes.PaymentCaptured,
                ResourceType,
                payment.Id,
                payment.Currency,
                "Payment captured (simulated)",
                [
                    new PostingRequest(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Debit, payment.Amount),
                    new PostingRequest(LedgerAccountKind.MerchantPayable, payment.MerchantId, PostingDirection.Credit, payment.Amount),
                ]),
            ct);

        if (payment.FeeAmount is > 0m and var fee)
        {
            await ledger.PostAsync(
                new JournalEntryRequest(
                    JournalEntryTypes.PaymentFee,
                    ResourceType,
                    payment.Id,
                    payment.Currency,
                    "Processing fee",
                    [
                        new PostingRequest(LedgerAccountKind.MerchantPayable, payment.MerchantId, PostingDirection.Debit, fee),
                        new PostingRequest(LedgerAccountKind.FeeRevenue, null, PostingDirection.Credit, fee),
                    ]),
                ct);
        }
    }

    private static class StatusCodes
    {
        public const int Created = 201;
    }
}
