namespace PayFlow.Modules.Payments.Contracts;

/// <summary>Payment states (docs/PAYMENT_LIFECYCLE.md §3).</summary>
public enum PaymentStatus
{
    Created,
    Processing,
    Pending,
    Succeeded,
    Declined,
    Failed,
    Expired,
    PartiallyRefunded,
    Refunded,
    Reversed,
}

/// <summary>Integration events written to the outbox when a payment changes state.</summary>
public static class PaymentEventTypes
{
    public const string Pending = "payment.pending";
    public const string Succeeded = "payment.succeeded";
    public const string Declined = "payment.declined";
    public const string Failed = "payment.failed";
    public const string Expired = "payment.expired";
    public const string Reversed = "payment.reversed";
}

/// <summary>Outbox payload: a snapshot of the payment when the event occurred.</summary>
public sealed record PaymentEvent(
    string PaymentId,
    string MerchantId,
    string MerchantReference,
    PaymentStatus Status,
    string Amount,
    string Currency,
    string? Fee,
    string? FailureCode,
    DateTimeOffset OccurredAt);
