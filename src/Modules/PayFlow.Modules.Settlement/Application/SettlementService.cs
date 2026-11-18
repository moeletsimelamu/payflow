using System.Globalization;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Settlement.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Settlement.Application;

public sealed record SettlementRunRequest(string? MerchantId, string? Currency, DateTimeOffset? Cutoff);

public sealed record PaySettlementRequest(bool SimulateFailure);

public sealed record SettlementItemResponse(SettlementItemType Type, string SourceId, string PaymentId, string Gross, string Fee, string Net, DateTimeOffset CompletedAt);

public sealed record SettlementBatchResponse(
    string Id,
    string MerchantId,
    string Currency,
    SettlementStatus Status,
    DateTimeOffset CutoffAt,
    int PaymentCount,
    int RefundCount,
    string GrossPayments,
    string TotalFees,
    string TotalRefunds,
    string CarriedForwardIn,
    string NetAmount,
    string PayoutAmount,
    string CarriedForwardOut,
    DateTimeOffset CreatedAt,
    DateTimeOffset? PaidAt,
    string? PayoutReference,
    string? FailureReason,
    bool Simulated,
    IReadOnlyList<SettlementItemResponse>? Items);

public sealed record SettlementSkip(string MerchantId, string Currency, string Reason);

public sealed record SettlementRunResponse(DateTimeOffset Cutoff, IReadOnlyList<SettlementBatchResponse> Created, IReadOnlyList<SettlementSkip> Skipped);

public sealed record SettlementPage(IReadOnlyList<SettlementBatchResponse> Items, int Page, int PageSize, int Total);

/// <summary>Outbox payload for <c>settlement.paid</c>.</summary>
public sealed record SettlementPaidEvent(string SettlementId, string MerchantId, string Currency, string NetAmount, string PayoutAmount, string CarriedForwardOut, string? PayoutReference, DateTimeOffset PaidAt);

/// <summary>Settlement runs and (simulated) payouts (SET-1..5, docs/PAYMENT_LIFECYCLE.md §6).</summary>
internal sealed class SettlementService(IServiceScopeFactory scopes, PayFlowDbContext db, IAuditLog audit, ILedger ledger, IOutbox outbox, IActorContext actor, TimeProvider clock)
{
    public const string SettlementPaidEventType = "settlement.paid";
    private const string Target = "settlement";

    public async Task<Result<SettlementRunResponse>> RunAsync(SettlementRunRequest request, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = request.Cutoff ?? new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero); // S1: T+1
        if (cutoff > now)
        {
            return Error.Validation("invalid_cutoff", "The cut-off cannot be in the future.");
        }

        Guid? merchantFilter = null;
        if (request.MerchantId is not null)
        {
            if (!PublicId.TryParse(request.MerchantId, IdPrefixes.Merchant, out var m))
            {
                return Error.Validation("invalid_merchant_id", "merchantId must be a mer_... id.");
            }

            merchantFilter = m;
        }

        if (request.Currency is not null && !SharedKernel.Money.Currency.TryFromCode(request.Currency, out _))
        {
            return Error.Validation("unsupported_currency", $"Currency '{request.Currency}' is not supported.");
        }

        await using var discovery = scopes.CreateAsyncScope();
        var targets = (await discovery.ServiceProvider.GetRequiredService<IPaymentSettlementFeed>().ListTargetsAsync(cutoff, ct))
            .Concat(await discovery.ServiceProvider.GetRequiredService<IRefundSettlementFeed>().ListTargetsAsync(cutoff, ct))
            .Distinct()
            .Where(t => (merchantFilter is null || t.MerchantId == merchantFilter) && (request.Currency is null || t.Currency == request.Currency))
            .OrderBy(t => t.MerchantId).ThenBy(t => t.Currency)
            .ToList();

        var created = new List<SettlementBatchResponse>();
        var skipped = new List<SettlementSkip>();
        var createdBy = actor.Actor.Id ?? actor.Actor.Display;
        foreach (var (merchantId, currency) in targets)
        {
            // Each merchant/currency in its own scope and transaction: one failure never blocks the others.
            await using var scope = scopes.CreateAsyncScope();
            var composer = scope.ServiceProvider.GetRequiredService<SettlementComposer>();
            var outcome = await composer.ComposeAsync(merchantId, currency, cutoff, createdBy, ct);
            if (outcome.IsSuccess)
            {
                created.Add(outcome.Value.ToResponse(includeItems: false));
            }
            else
            {
                skipped.Add(new SettlementSkip(PublicId.Create(IdPrefixes.Merchant, merchantId), currency, outcome.Error.Code));
            }
        }

        return new SettlementRunResponse(cutoff, created, skipped);
    }

    /// <summary>Simulated bank payout (S6). Failure is simulated on request so the retry path can be demonstrated.</summary>
    public async Task<Result<SettlementBatchResponse>> PayAsync(Guid batchId, PaySettlementRequest request, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var batch = await db.Set<SettlementBatch>().Include(b => b.Items).SingleOrDefaultAsync(b => b.Id == batchId, ct);
        if (batch is null)
        {
            return NotFound();
        }

        var now = clock.GetUtcNow();
        var batchPublicId = PublicId.Create(IdPrefixes.Settlement, batch.Id);
        if (request.SimulateFailure)
        {
            var failed = batch.MarkFailed("Simulated bank rejection", now);
            if (!failed.IsSuccess)
            {
                return failed.Error;
            }

            audit.Record(new AuditEntry("settlement.payout.failed", Target, batchPublicId) { MerchantId = batch.MerchantId, Changes = new { batch.FailureReason } });
        }
        else
        {
            var reference = $"SIM-PAYOUT-{batch.Id.ToString("N")[..12].ToUpperInvariant()}";
            var paid = batch.MarkPaid(reference, now);
            if (!paid.IsSuccess)
            {
                return paid.Error;
            }

            await ledger.PostAsync(
                new JournalEntryRequest(
                    JournalEntryTypes.SettlementPaid, Target, batch.Id, batch.Currency, $"Simulated payout {reference}",
                    [
                        new PostingRequest(LedgerAccountKind.MerchantPayable, batch.MerchantId, PostingDirection.Debit, batch.PayoutAmount),
                        new PostingRequest(LedgerAccountKind.PayoutCash, null, PostingDirection.Credit, batch.PayoutAmount),
                    ]),
                ct);
            outbox.Enqueue(SettlementPaidEventType, ToEvent(batch));
            audit.Record(new AuditEntry("settlement.payout.paid", Target, batchPublicId)
            {
                MerchantId = batch.MerchantId,
                Changes = new { payout = batch.PayoutAmount, batch.Currency, reference },
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return batch.ToResponse(includeItems: false);
    }

    public async Task<SettlementPage> ListAsync(Guid? merchantId, string? currency, SettlementStatus? status, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Set<SettlementBatch>().AsNoTracking();
        if (merchantId is { } m)
        {
            query = query.Where(b => b.MerchantId == m);
        }

        if (!string.IsNullOrWhiteSpace(currency))
        {
            query = query.Where(b => b.Currency == currency);
        }

        if (status is { } s)
        {
            query = query.Where(b => b.Status == s);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(b => b.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new SettlementPage(rows.Select(b => b.ToResponse(includeItems: false)).ToList(), page, pageSize, total);
    }

    public async Task<Result<SettlementBatchResponse>> GetAsync(Guid batchId, Guid? merchantScope, CancellationToken ct)
    {
        var batch = await FindAsync(batchId, merchantScope, ct);
        return batch is null ? NotFound() : batch.ToResponse(includeItems: true);
    }

    /// <summary>SET-5: line items as CSV for the merchant's own reconciliation.</summary>
    public async Task<Result<string>> ExportCsvAsync(Guid batchId, Guid merchantScope, CancellationToken ct)
    {
        var batch = await FindAsync(batchId, merchantScope, ct);
        if (batch is null)
        {
            return NotFound();
        }

        var csv = new StringBuilder("settlement_id,type,source_id,payment_id,completed_at,gross,fee,net,currency\n");
        foreach (var i in batch.Items.OrderBy(i => i.CompletedAt))
        {
            var r = i.ToResponse(batch.Currency);
            csv.Append(CultureInfo.InvariantCulture,
                $"{PublicId.Create(IdPrefixes.Settlement, batch.Id)},{r.Type},{r.SourceId},{r.PaymentId},{r.CompletedAt:O},{r.Gross},{r.Fee},{r.Net},{batch.Currency}\n");
        }

        return csv.ToString();
    }

    private Task<SettlementBatch?> FindAsync(Guid batchId, Guid? merchantScope, CancellationToken ct) =>
        db.Set<SettlementBatch>().AsNoTracking().Include(b => b.Items)
            .SingleOrDefaultAsync(b => b.Id == batchId && (merchantScope == null || b.MerchantId == merchantScope), ct);

    internal static SettlementPaidEvent ToEvent(SettlementBatch b) => new(
        PublicId.Create(IdPrefixes.Settlement, b.Id),
        PublicId.Create(IdPrefixes.Merchant, b.MerchantId),
        b.Currency,
        Format(b.NetAmount, b.Currency),
        Format(b.PayoutAmount, b.Currency),
        Format(b.CarriedForwardOut, b.Currency),
        b.PayoutReference,
        b.PaidAt!.Value);

    internal static string Format(decimal amount, string currency) => Money.Of(amount, currency).ToAmountString();

    private static Error NotFound() => Error.NotFound("settlement_not_found", "Settlement batch not found.");
}

/// <summary>Composes one batch for one merchant and currency, in one transaction.</summary>
internal sealed class SettlementComposer(
    PayFlowDbContext db,
    IPaymentSettlementFeed payments,
    IRefundSettlementFeed refunds,
    IAuditLog audit,
    IOutbox outbox,
    IdGenerator ids,
    TimeProvider clock)
{
    public async Task<Result<SettlementBatch>> ComposeAsync(Guid merchantId, string currency, DateTimeOffset cutoff, string createdBy, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);

            // Serialises runs for this merchant/currency across threads and instances; the unique
            // (item_type, source_id) index is the guarantee even without it (S5).
            var lockKey = $"settlement:{merchantId:N}:{currency}";
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

            if (await db.Set<SettlementBatch>().AnyAsync(b => b.MerchantId == merchantId && b.Currency == currency && b.Status != SettlementStatus.Paid, ct))
            {
                return Error.Conflict("open_batch_exists", "An unpaid batch exists for this merchant and currency; pay or retry it first.");
            }

            var lockedPayments = await payments.LockUnsettledAsync(merchantId, currency, cutoff, ct);
            var lockedRefunds = await refunds.LockUnsettledAsync(merchantId, currency, cutoff, ct);
            if (lockedPayments.Count + lockedRefunds.Count == 0)
            {
                return Error.Unprocessable("nothing_to_settle", "No eligible payments or refunds before the cut-off.");
            }

            var carryIn = await db.Set<SettlementBatch>()
                .Where(b => b.MerchantId == merchantId && b.Currency == currency)
                .OrderByDescending(b => b.CreatedAt)
                .Select(b => (decimal?)b.CarriedForwardOut)
                .FirstOrDefaultAsync(ct) ?? 0m;

            var batch = SettlementBatch.Compose(ids.NewId(), merchantId, currency, cutoff, carryIn, lockedPayments, lockedRefunds, createdBy, clock.GetUtcNow());
            db.Set<SettlementBatch>().Add(batch);
            await payments.MarkSettledAsync(lockedPayments.Select(p => p.PaymentId).ToList(), batch.Id, ct);
            await refunds.MarkSettledAsync(lockedRefunds.Select(r => r.RefundId).ToList(), batch.Id, ct);
            audit.Record(new AuditEntry("settlement.batch.created", "settlement", PublicId.Create(IdPrefixes.Settlement, batch.Id))
            {
                MerchantId = merchantId,
                Changes = new { currency, batch.PaymentCount, batch.RefundCount, batch.NetAmount, batch.PayoutAmount, batch.CarriedForwardIn, batch.CarriedForwardOut, cutoff },
            });
            if (batch.Status == SettlementStatus.Paid)
            {
                outbox.Enqueue(SettlementService.SettlementPaidEventType, SettlementService.ToEvent(batch)); // nothing to pay out
            }

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return batch;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return Error.Conflict("concurrent_settlement", "Another run settled these items first.");
        }
    }
}

internal static class SettlementMapping
{
    public static SettlementBatchResponse ToResponse(this SettlementBatch b, bool includeItems) => new(
        PublicId.Create(IdPrefixes.Settlement, b.Id),
        PublicId.Create(IdPrefixes.Merchant, b.MerchantId),
        b.Currency,
        b.Status,
        b.CutoffAt,
        b.PaymentCount,
        b.RefundCount,
        SettlementService.Format(b.GrossPayments, b.Currency),
        SettlementService.Format(b.TotalFees, b.Currency),
        SettlementService.Format(b.TotalRefunds, b.Currency),
        SettlementService.Format(b.CarriedForwardIn, b.Currency),
        SettlementService.Format(b.NetAmount, b.Currency),
        SettlementService.Format(b.PayoutAmount, b.Currency),
        SettlementService.Format(b.CarriedForwardOut, b.Currency),
        b.CreatedAt,
        b.PaidAt,
        b.PayoutReference,
        b.FailureReason,
        Simulated: true,
        includeItems ? b.Items.OrderBy(i => i.CompletedAt).Select(i => i.ToResponse(b.Currency)).ToList() : null);

    public static SettlementItemResponse ToResponse(this SettlementItem i, string currency) => new(
        i.ItemType,
        PublicId.Create(i.ItemType == SettlementItemType.Payment ? IdPrefixes.Payment : IdPrefixes.Refund, i.SourceId),
        PublicId.Create(IdPrefixes.Payment, i.PaymentId),
        SettlementService.Format(i.GrossAmount, currency),
        SettlementService.Format(i.FeeAmount, currency),
        SettlementService.Format(i.NetAmount, currency),
        i.CompletedAt);
}

/// <summary>Rule R8: reversals only before settlement. Backed by the unique settlement items.</summary>
internal sealed class SettlementLookup(PayFlowDbContext db) : ISettlementLookup
{
    public Task<bool> IsPaymentSettledAsync(Guid paymentId, CancellationToken cancellationToken) =>
        db.Set<SettlementItem>().AnyAsync(i => i.ItemType == SettlementItemType.Payment && i.SourceId == paymentId, cancellationToken);
}
