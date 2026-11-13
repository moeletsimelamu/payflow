namespace PayFlow.Modules.Refunds.Contracts;

public enum RefundStatus
{
    Pending,
    Succeeded,
    Failed,
}

public static class RefundEventTypes
{
    public const string Succeeded = "refund.succeeded";
    public const string Failed = "refund.failed";
}

/// <summary>Outbox payload for refund events.</summary>
public sealed record RefundEvent(
    string RefundId,
    string PaymentId,
    string MerchantId,
    RefundStatus Status,
    string Amount,
    string Currency,
    string? FailureReason,
    DateTimeOffset OccurredAt);

/// <summary>Outbox payload for <c>payment.reversed</c>.</summary>
public sealed record ReversalEvent(
    string ReversalId,
    string PaymentId,
    string MerchantId,
    string Amount,
    string FeeReturned,
    string Currency,
    DateTimeOffset OccurredAt);

/// <summary>
/// Whether a payment is already in a settlement batch (rule R8: reversals only before settlement).
/// Phase 7's Settlement module provides the real implementation; until then nothing is settled.
/// </summary>
public interface ISettlementLookup
{
    Task<bool> IsPaymentSettledAsync(Guid paymentId, CancellationToken cancellationToken);
}
