namespace PayFlow.Modules.Refunds.Contracts;

/// <summary>A succeeded refund that has not been settled yet.</summary>
public sealed record SettleableRefund(Guid RefundId, Guid PaymentId, decimal Amount, DateTimeOffset CompletedAt);

/// <summary>What the Settlement module may read and change on refunds. The Refunds module stays the only writer.</summary>
public interface IRefundSettlementFeed
{
    Task<IReadOnlyList<(Guid MerchantId, string Currency)>> ListTargetsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Locks (<c>FOR UPDATE</c>) and returns succeeded, unsettled refunds completed before the cut-off. Requires an open transaction.</summary>
    Task<IReadOnlyList<SettleableRefund>> LockUnsettledAsync(Guid merchantId, string currency, DateTimeOffset cutoff, CancellationToken cancellationToken);

    Task MarkSettledAsync(IReadOnlyCollection<Guid> refundIds, Guid batchId, CancellationToken cancellationToken);
}

public sealed record RefundRecord(Guid RefundId, Guid PaymentId, Guid MerchantId, decimal Amount, string Currency, RefundStatus Status, DateTimeOffset? CompletedAt);

/// <summary>Read-only refund lookups across all merchants, for platform reconciliation.</summary>
public interface IRefundLookup
{
    Task<IReadOnlyDictionary<Guid, RefundRecord>> FindAsync(IReadOnlyCollection<Guid> refundIds, CancellationToken cancellationToken);

    /// <summary>Refunds an acquirer statement should contain: succeeded, completed in [from, to).</summary>
    Task<IReadOnlyList<RefundRecord>> ListAcquirerVisibleAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
