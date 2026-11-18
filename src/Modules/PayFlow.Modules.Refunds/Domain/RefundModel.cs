using PayFlow.Modules.Refunds.Contracts;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Refunds.Domain;

/// <summary>
/// A refund of part or all of a payment. A new record, never an edit of the payment (rule R9).
/// States: Pending → Succeeded | Failed (rule REF-4).
/// </summary>
internal sealed class Refund
{
    private Refund()
    {
    }

    public Guid Id { get; private init; }

    public Guid PaymentId { get; private init; }

    public Guid MerchantId { get; private init; }

    public decimal Amount { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public RefundStatus Status { get; private set; }

    public string? Reason { get; private init; }

    public string? FailureReason { get; private set; }

    /// <summary>Simulation token of the refunded payment; decides the simulated refund outcome.</summary>
    public string PaymentMethod { get; private init; } = string.Empty;

    public string RequestedByType { get; private init; } = string.Empty;

    public string? RequestedById { get; private init; }

    public string IdempotencyKey { get; private init; } = string.Empty;

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public uint Version { get; private set; }

    /// <summary>The settlement batch that netted this refund off a payout; null until settled.</summary>
    public Guid? SettlementBatchId { get; private set; }

    public void MarkSettled(Guid batchId)
    {
        if (Status != RefundStatus.Succeeded)
        {
            throw new DomainException(Error.Unprocessable("refund_not_settleable", $"A {Status} refund cannot be settled."));
        }

        if (SettlementBatchId is not null)
        {
            throw new DomainException(Error.Conflict("refund_already_settled", "This refund is already in a settlement batch."));
        }

        SettlementBatchId = batchId;
    }

    public static Refund Request(Guid id, Guid paymentId, Guid merchantId, decimal amount, string currency, string? reason, string paymentMethod, string idempotencyKey, Actor actor, DateTimeOffset now) => new()
    {
        Id = id,
        PaymentId = paymentId,
        MerchantId = merchantId,
        Amount = amount,
        Currency = currency,
        Reason = reason,
        PaymentMethod = paymentMethod,
        IdempotencyKey = idempotencyKey,
        RequestedByType = actor.Type.ToString(),
        RequestedById = actor.Id,
        Status = RefundStatus.Pending,
        CreatedAt = now,
    };

    public void Succeed(DateTimeOffset now)
    {
        EnsurePending();
        Status = RefundStatus.Succeeded;
        CompletedAt = now;
    }

    public void Fail(string reason, DateTimeOffset now)
    {
        EnsurePending();
        Status = RefundStatus.Failed;
        FailureReason = reason;
        CompletedAt = now;
    }

    private void EnsurePending()
    {
        if (Status != RefundStatus.Pending)
        {
            throw new DomainException(Error.Unprocessable("refund_not_pending", $"Refund is already {Status}."));
        }
    }
}

/// <summary>Full cancellation of a payment before settlement, including the fee (rules R8, REF-5).</summary>
internal sealed class Reversal
{
    public Guid Id { get; init; }

    public Guid PaymentId { get; init; }

    public Guid MerchantId { get; init; }

    public decimal Amount { get; init; }

    public decimal FeeReturned { get; init; }

    public required string Currency { get; init; }

    public required string Reason { get; init; }

    public required string RequestedByType { get; init; }

    public string? RequestedById { get; init; }

    public required string IdempotencyKey { get; init; }

    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>Deterministic refund outcomes: refunds fail only for payments made with <c>sim_card_refund_fails</c>.</summary>
internal static class RefundSimulator
{
    public const string FailingMethod = "sim_card_refund_fails";
    public const string FailureReason = "The simulated processor rejected the refund.";

    public static bool Succeeds(string paymentMethod) => !string.Equals(paymentMethod, FailingMethod, StringComparison.Ordinal);
}
