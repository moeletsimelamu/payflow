using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Domain;

namespace PayFlow.Modules.Refunds.Application;

internal sealed class RefundSettlementFeed(PayFlowDbContext db) : IRefundSettlementFeed, IRefundLookup
{
    public async Task<IReadOnlyList<(Guid MerchantId, string Currency)>> ListTargetsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        (await db.Set<Refund>().AsNoTracking()
            .Where(r => r.Status == RefundStatus.Succeeded && r.CompletedAt < cutoff && r.SettlementBatchId == null)
            .Select(r => new { r.MerchantId, r.Currency })
            .Distinct()
            .ToListAsync(cancellationToken))
        .Select(x => (x.MerchantId, x.Currency))
        .ToList();

    public async Task<IReadOnlyList<SettleableRefund>> LockUnsettledAsync(Guid merchantId, string currency, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Locking refunds for settlement requires an open transaction.");
        }

        var ids = await db.Database.SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM refunds.refunds
                 WHERE merchant_id = {merchantId} AND currency = {currency}
                   AND status = 'Succeeded' AND completed_at < {cutoff} AND settlement_batch_id IS NULL
                 ORDER BY id
                   FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        var refunds = await db.Set<Refund>().Where(r => ids.Contains(r.Id)).ToListAsync(cancellationToken);
        return refunds
            .OrderBy(r => r.CompletedAt)
            .Select(r => new SettleableRefund(r.Id, r.PaymentId, r.Amount, r.CompletedAt!.Value))
            .ToList();
    }

    public async Task MarkSettledAsync(IReadOnlyCollection<Guid> refundIds, Guid batchId, CancellationToken cancellationToken)
    {
        var refunds = await db.Set<Refund>().Where(r => refundIds.Contains(r.Id)).ToListAsync(cancellationToken);
        if (refunds.Count != refundIds.Count)
        {
            throw new InvalidOperationException("Some refunds to settle were not found.");
        }

        foreach (var refund in refunds)
        {
            refund.MarkSettled(batchId);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, RefundRecord>> FindAsync(IReadOnlyCollection<Guid> refundIds, CancellationToken cancellationToken) =>
        await db.Set<Refund>().AsNoTracking()
            .Where(r => refundIds.Contains(r.Id))
            .Select(r => new RefundRecord(r.Id, r.PaymentId, r.MerchantId, r.Amount, r.Currency, r.Status, r.CompletedAt))
            .ToDictionaryAsync(r => r.RefundId, cancellationToken);

    public async Task<IReadOnlyList<RefundRecord>> ListAcquirerVisibleAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await db.Set<Refund>().AsNoTracking()
            .Where(r => r.Status == RefundStatus.Succeeded && r.CompletedAt >= from && r.CompletedAt < to)
            .OrderBy(r => r.CompletedAt)
            .Select(r => new RefundRecord(r.Id, r.PaymentId, r.MerchantId, r.Amount, r.Currency, r.Status, r.CompletedAt))
            .ToListAsync(cancellationToken);
}
