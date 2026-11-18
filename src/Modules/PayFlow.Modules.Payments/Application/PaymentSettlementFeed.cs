using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;

namespace PayFlow.Modules.Payments.Application;

internal sealed class PaymentSettlementFeed(PayFlowDbContext db) : IPaymentSettlementFeed, IPaymentLookup
{
    private static readonly PaymentStatus[] Captured = [PaymentStatus.Succeeded, PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded];

    public async Task<IReadOnlyList<(Guid MerchantId, string Currency)>> ListTargetsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken) =>
        (await db.Set<Payment>().AsNoTracking()
            .Where(p => Captured.Contains(p.Status) && p.CompletedAt < cutoff && p.SettlementBatchId == null)
            .Select(p => new { p.MerchantId, p.Currency })
            .Distinct()
            .ToListAsync(cancellationToken))
        .Select(x => (x.MerchantId, x.Currency))
        .ToList();

    public async Task<IReadOnlyList<SettleablePayment>> LockUnsettledAsync(Guid merchantId, string currency, DateTimeOffset cutoff, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Locking payments for settlement requires an open transaction.");
        }

        var ids = await db.Database.SqlQuery<Guid>(
                $"""
                SELECT id AS "Value" FROM payments.payments
                 WHERE merchant_id = {merchantId} AND currency = {currency}
                   AND status IN ('Succeeded', 'PartiallyRefunded', 'Refunded')
                   AND completed_at < {cutoff} AND settlement_batch_id IS NULL
                 ORDER BY id
                   FOR UPDATE
                """)
            .ToListAsync(cancellationToken);
        if (ids.Count == 0)
        {
            return [];
        }

        // Fresh, tracked read under the lock.
        var payments = await db.Set<Payment>().Where(p => ids.Contains(p.Id)).ToListAsync(cancellationToken);
        return payments
            .OrderBy(p => p.CompletedAt)
            .Select(p => new SettleablePayment(p.Id, p.Amount, p.FeeAmount ?? 0m, p.CompletedAt!.Value))
            .ToList();
    }

    public async Task MarkSettledAsync(IReadOnlyCollection<Guid> paymentIds, Guid batchId, CancellationToken cancellationToken)
    {
        var payments = await db.Set<Payment>().Where(p => paymentIds.Contains(p.Id)).ToListAsync(cancellationToken);
        if (payments.Count != paymentIds.Count)
        {
            throw new InvalidOperationException("Some payments to settle were not found.");
        }

        foreach (var payment in payments)
        {
            payment.MarkSettled(batchId);
        }
    }

    public async Task<IReadOnlyDictionary<Guid, PaymentRecord>> FindAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken) =>
        await db.Set<Payment>().AsNoTracking()
            .Where(p => paymentIds.Contains(p.Id))
            .Select(p => new PaymentRecord(p.Id, p.MerchantId, p.Amount, p.Currency, p.Status, p.CompletedAt))
            .ToDictionaryAsync(p => p.PaymentId, cancellationToken);

    public async Task<IReadOnlyList<PaymentRecord>> ListAcquirerVisibleAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken) =>
        await db.Set<Payment>().AsNoTracking()
            .Where(p => (Captured.Contains(p.Status) || p.Status == PaymentStatus.Reversed) && p.CompletedAt >= from && p.CompletedAt < to)
            .OrderBy(p => p.CompletedAt)
            .Select(p => new PaymentRecord(p.Id, p.MerchantId, p.Amount, p.Currency, p.Status, p.CompletedAt))
            .ToListAsync(cancellationToken);
}
