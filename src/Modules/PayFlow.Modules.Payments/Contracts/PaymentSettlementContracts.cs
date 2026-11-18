namespace PayFlow.Modules.Payments.Contracts;

/// <summary>A captured payment that has not been settled yet.</summary>
public sealed record SettleablePayment(Guid PaymentId, decimal Amount, decimal Fee, DateTimeOffset CompletedAt);

/// <summary>
/// What the Settlement module may read and change on payments (docs/PAYMENT_LIFECYCLE.md §6).
/// The Payments module stays the only writer of payment rows.
/// </summary>
public interface IPaymentSettlementFeed
{
    /// <summary>(merchant, currency) pairs that have unsettled captured payments completed before <paramref name="cutoff"/>.</summary>
    Task<IReadOnlyList<(Guid MerchantId, string Currency)>> ListTargetsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>
    /// Locks (<c>FOR UPDATE</c>, the same row lock refunds and reversals take) and returns every payment
    /// eligible under rule S2. Requires an open transaction; the lock holds until it commits.
    /// </summary>
    Task<IReadOnlyList<SettleablePayment>> LockUnsettledAsync(Guid merchantId, string currency, DateTimeOffset cutoff, CancellationToken cancellationToken);

    /// <summary>Records the batch on the payments locked by <see cref="LockUnsettledAsync"/>. Saved with the caller's unit of work.</summary>
    Task MarkSettledAsync(IReadOnlyCollection<Guid> paymentIds, Guid batchId, CancellationToken cancellationToken);
}

/// <summary>A payment as the reconciliation engine sees it.</summary>
public sealed record PaymentRecord(Guid PaymentId, Guid MerchantId, decimal Amount, string Currency, PaymentStatus Status, DateTimeOffset? CompletedAt);

/// <summary>Read-only payment lookups across all merchants, for platform reconciliation.</summary>
public interface IPaymentLookup
{
    Task<IReadOnlyDictionary<Guid, PaymentRecord>> FindAsync(IReadOnlyCollection<Guid> paymentIds, CancellationToken cancellationToken);

    /// <summary>Payments an acquirer statement should contain: captured or reversed, completed in [from, to).</summary>
    Task<IReadOnlyList<PaymentRecord>> ListAcquirerVisibleAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken);
}
