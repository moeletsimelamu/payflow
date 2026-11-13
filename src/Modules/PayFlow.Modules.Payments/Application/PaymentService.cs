using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Payments.Application;

/// <summary>The stored or freshly produced HTTP response of a create request.</summary>
public sealed record PaymentCreation(int StatusCode, string Body, bool Replayed, string? PaymentId);

/// <summary>
/// Payment initiation (PAY-1..5) with idempotency (IDEM-1..5). The flow, in order:
/// <list type="number">
/// <item>reject card-like data and claim the idempotency key (committed on its own);</item>
/// <item>check merchant status, currency and limits; release the key if rejected;</item>
/// <item>transaction 1: create the payment in Processing and link it to the key;</item>
/// <item>ask the deterministic simulator;</item>
/// <item>transaction 2 (<see cref="PaymentProcessor"/>): outcome, ledger, outbox, stored response.</item>
/// </list>
/// A crash between 3 and 5 leaves a Processing payment that <see cref="ProcessingRecoveryJob"/> completes.
/// </summary>
internal sealed class PaymentService(
    PayFlowDbContext db,
    IdempotencyStore idempotency,
    IMerchantDirectory merchants,
    PaymentProcessor processor,
    IActorContext actorContext,
    ICorrelationContext correlation,
    IdGenerator ids,
    TimeProvider clock,
    IOptions<JsonOptions> json,
    ILogger<PaymentService> logger)
{
    public const string Operation = "payments.create";
    private const string Route = "/v1/payments";

    public async Task<Result<PaymentCreation>> CreateAsync(Guid merchantId, string? idempotencyKeyHeader, CreatePaymentRequest request, CancellationToken ct)
    {
        var begun = await BeginAsync(merchantId, idempotencyKeyHeader, request, ct);
        if (!begun.IsSuccess)
        {
            return begun.Error;
        }

        if (begun.Value.Replay is { } replay)
        {
            return replay;
        }

        // Transaction 2. If this throws, the key stays InProgress and crash recovery completes the payment.
        var payment = begun.Value.Payment!;
        var decision = PaymentSimulator.Decide(payment.PaymentMethod);
        var body = (await processor.CompleteProcessingAsync(payment, decision, actorContext.Actor, ct)).Body;
        logger.LogInformation("Payment {PaymentId} for merchant {MerchantId} ended {Status}.",
            PublicId.Create(IdPrefixes.Payment, payment.Id), PublicId.Create(IdPrefixes.Merchant, merchantId), payment.Status);
        return new PaymentCreation(201, body, Replayed: false, PublicId.Create(IdPrefixes.Payment, payment.Id));
    }

    internal sealed record BeginOutcome(Payment? Payment, PaymentCreation? Replay);

    /// <summary>Steps 1–3: everything up to and including transaction 1. Exposed for crash-recovery tests.</summary>
    internal async Task<Result<BeginOutcome>> BeginAsync(Guid merchantId, string? idempotencyKeyHeader, CreatePaymentRequest request, CancellationToken ct)
    {
        var key = IdempotencyKeys.Validate(idempotencyKeyHeader);
        if (!key.IsSuccess)
        {
            return key.Error;
        }

        if (ContainsCardData(request))
        {
            return Error.Validation("card_data_not_accepted", "PayFlow is a simulation and never accepts card numbers. Use a simulation token such as sim_card_success.");
        }

        var keyRef = new IdempotencyKeyRef($"merchant:{merchantId:N}", Operation, key.Value);
        var hash = IdempotencyKeys.HashRequest("POST", Route, request, json.Value.SerializerOptions);
        var claim = await idempotency.ClaimAsync(keyRef, hash, ct);
        switch (claim.Kind)
        {
            case IdempotencyClaimKind.Mismatch:
                return Error.Unprocessable("idempotency_key_reused", "This Idempotency-Key was already used with a different request.");
            case IdempotencyClaimKind.InProgress:
                return Error.Conflict("idempotency_request_in_progress", "A request with this Idempotency-Key is still being processed. Retry shortly.");
            case IdempotencyClaimKind.Replay:
                return new BeginOutcome(null, new PaymentCreation(claim.ResponseStatus!.Value, claim.ResponseBody!, Replayed: true, PaymentId: null));
        }

        Payment payment;
        try
        {
            var prepared = await PrepareAsync(merchantId, key.Value, request, ct);
            if (!prepared.IsSuccess)
            {
                await idempotency.ReleaseAsync(keyRef);
                return prepared.Error;
            }

            payment = prepared.Value;

            // Transaction 1: the payment exists, in Processing, linked to the key.
            await using (var transaction = await db.Database.BeginTransactionAsync(ct))
            {
                db.Set<Payment>().Add(payment);
                await db.SaveChangesAsync(ct);
                await idempotency.AttachResourceAsync(keyRef, PaymentProcessor.ResourceType, payment.Id, ct);
                await transaction.CommitAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "ix_payments_merchant_id_idempotency_key" })
        {
            // The key's record expired, but the payment it created still exists: never create a second one.
            await idempotency.ReleaseAsync(keyRef);
            return Error.Conflict("duplicate_idempotency_key", "A payment was already created with this Idempotency-Key. Retrieve it with GET /v1/payments.");
        }
        catch
        {
            await idempotency.ReleaseAsync(keyRef);
            throw;
        }

        return new BeginOutcome(payment, null);
    }

    private async Task<Result<Payment>> PrepareAsync(Guid merchantId, string idempotencyKey, CreatePaymentRequest request, CancellationToken ct)
    {
        if (!Currency.TryFromCode(request.Currency, out var currency))
        {
            return Error.Validation("unsupported_currency", $"Currency '{request.Currency}' is not supported.");
        }

        if (!Money.TryParse(request.Amount, currency, out var amount) || !amount.IsPositive)
        {
            return Error.Validation("invalid_amount", $"amount must be a positive decimal string with at most {currency.MinorUnits} decimal places for {currency.Code}.");
        }

        var profile = await merchants.GetPaymentProfileAsync(merchantId, currency.Code, ct);
        if (profile is null || profile.Status != MerchantStatus.Active)
        {
            return Error.Unprocessable("merchant_not_active", "Only active merchants can accept payments.");
        }

        if (profile.Terms is not { } terms)
        {
            return Error.Unprocessable("currency_not_enabled", $"{currency.Code} is not enabled for this merchant.");
        }

        if (amount.Amount < terms.MinPaymentAmount)
        {
            return Error.Unprocessable("amount_below_minimum", $"The minimum payment is {Money.Of(terms.MinPaymentAmount, currency)}.");
        }

        if (amount.Amount > terms.MaxPaymentAmount)
        {
            return Error.Unprocessable("amount_above_maximum", $"The maximum payment is {Money.Of(terms.MaxPaymentAmount, currency)}.");
        }

        var metadata = request.Metadata is { Count: > 0 } m ? JsonSerializer.Serialize(m) : null;
        return Payment.Initiate(
            ids.NewId(),
            merchantId,
            amount,
            request.PaymentMethod,
            request.MerchantReference.Trim(),
            request.Description?.Trim(),
            metadata,
            idempotencyKey,
            new FeeSnapshot(terms.FeePlanId, terms.PercentageRate, terms.FixedFee),
            clock.GetUtcNow(),
            actorContext.Actor,
            correlation.CorrelationId);
    }

    private static bool ContainsCardData(CreatePaymentRequest r) =>
        CardDataDetector.ContainsCardNumber(r.MerchantReference) ||
        CardDataDetector.ContainsCardNumber(r.Description) ||
        CardDataDetector.ContainsCardNumber(r.PaymentMethod) ||
        CardDataDetector.ContainsCardNumber(r.Amount) ||
        (r.Metadata?.Any(kv => CardDataDetector.ContainsCardNumber(kv.Key) || CardDataDetector.ContainsCardNumber(kv.Value)) ?? false);

    internal static string FormatRate(decimal rate) => rate.ToString("0.######", CultureInfo.InvariantCulture);
}
