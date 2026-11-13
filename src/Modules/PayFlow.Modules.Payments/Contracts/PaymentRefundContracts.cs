using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Payments.Contracts;

/// <summary>A payment's money position, as seen by the Refunds module.</summary>
public sealed record PaymentSnapshot(
    Guid PaymentId,
    Guid MerchantId,
    PaymentStatus Status,
    decimal Amount,
    string Currency,
    decimal? Fee,
    decimal RefundedAmount,
    decimal PendingRefundAmount,
    string PaymentMethod)
{
    /// <summary>docs/PAYMENT_LIFECYCLE.md §4: amount − refunded − pending.</summary>
    public decimal RefundableAmount => Amount - RefundedAmount - PendingRefundAmount;
}

/// <summary>
/// The only way other modules change a payment's refund position or reverse it. The Payments module
/// remains the sole writer of payment rows; callers save through the shared unit of work.
/// <para>
/// Every mutating call requires an open database transaction and a prior <see cref="LockAsync"/>,
/// which takes a row lock (<c>SELECT … FOR UPDATE</c>) held until commit (rule R4). Refund creation,
/// refund completion, reversal and (Phase 7) settlement all take this same lock, so they serialise
/// per payment and can never interleave.
/// </para>
/// </summary>
public interface IPaymentRefundOperations
{
    Task<PaymentSnapshot?> FindAsync(Guid merchantId, Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Locks the payment row for the rest of the current transaction and returns its fresh state.</summary>
    Task<PaymentSnapshot?> LockAsync(Guid merchantId, Guid paymentId, CancellationToken cancellationToken);

    /// <summary>Reserves <paramref name="amount"/> against the refundable balance (rule R3).</summary>
    Task<Result<PaymentSnapshot>> ReserveRefundAsync(Guid paymentId, decimal amount, CancellationToken cancellationToken);

    /// <summary>Releases a reservation; on success also books it as refunded and moves the status (rule R5).</summary>
    Task<PaymentSnapshot> CompleteRefundAsync(Guid paymentId, decimal amount, bool succeeded, Actor actor, CancellationToken cancellationToken);

    /// <summary>Full cancellation of an eligible payment (rule R8).</summary>
    Task<Result<PaymentSnapshot>> ReverseAsync(Guid paymentId, bool isSettled, string reason, Actor actor, CancellationToken cancellationToken);
}
