using PayFlow.Modules.Payments.Contracts;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Payments.Domain;

/// <summary>One row of the append-only status history.</summary>
internal sealed class PaymentStatusChange
{
    public long Id { get; init; }

    public Guid PaymentId { get; init; }

    public PaymentStatus? FromStatus { get; init; }

    public PaymentStatus ToStatus { get; init; }

    public required string Reason { get; init; }

    public required string ActorType { get; init; }

    public string? ActorId { get; init; }

    public required string CorrelationId { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}

/// <summary>Fee terms copied onto the payment at creation, so later fee plan changes never alter it.</summary>
internal sealed record FeeSnapshot(Guid FeePlanId, decimal Rate, decimal Fixed);

/// <summary>
/// Payment aggregate. Every state change goes through <see cref="TransitionTo"/>, which enforces the
/// whitelist in <see cref="PaymentStateMachine"/> and appends to the status history.
/// </summary>
internal sealed class Payment
{
    private readonly List<PaymentStatusChange> _history = [];

    private Payment()
    {
    }

    public Guid Id { get; private init; }

    public Guid MerchantId { get; private init; }

    public string MerchantReference { get; private init; } = string.Empty;

    public decimal Amount { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public PaymentStatus Status { get; private set; }

    public string PaymentMethod { get; private init; } = string.Empty;

    public string? FailureCode { get; private set; }

    public string? FailureReason { get; private set; }

    public Guid FeePlanId { get; private init; }

    public decimal FeeRate { get; private init; }

    public decimal FeeFixed { get; private init; }

    public decimal? FeeAmount { get; private set; }

    public decimal RefundedAmount { get; private set; }

    public decimal PendingRefundAmount { get; private set; }

    public string IdempotencyKey { get; private init; } = string.Empty;

    public string? Description { get; private init; }

    /// <summary>Merchant metadata as a JSON object (jsonb).</summary>
    public string? Metadata { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public DateTimeOffset? ProcessingStartedAt { get; private set; }

    public DateTimeOffset? CompletedAt { get; private set; }

    public DateTimeOffset? SimulatedResolveAt { get; private set; }

    public uint Version { get; private set; }

    /// <summary>The settlement batch that paid this payment out to the merchant; null until settled (rule S5).</summary>
    public Guid? SettlementBatchId { get; private set; }

    /// <summary>Rule S2/S5: only captured payments settle, and only once.</summary>
    public void MarkSettled(Guid batchId)
    {
        if (!PaymentStateMachine.IsCaptured(Status) || Status == PaymentStatus.Reversed)
        {
            throw new DomainException(Error.Unprocessable("payment_not_settleable", $"A {Status} payment cannot be settled."));
        }

        if (SettlementBatchId is not null)
        {
            throw new DomainException(Error.Conflict("payment_already_settled", "This payment is already in a settlement batch."));
        }

        SettlementBatchId = batchId;
    }

    public IReadOnlyList<PaymentStatusChange> History => _history;

    public Money AmountMoney => Money.Of(Amount, Currency);

    public Money? FeeMoney => FeeAmount is { } fee ? Money.Of(fee, Currency) : null;

    /// <summary>Creates the payment and hands it to the processor (Created → Processing).</summary>
    public static Payment Initiate(
        Guid id,
        Guid merchantId,
        Money amount,
        string paymentMethod,
        string merchantReference,
        string? description,
        string? metadata,
        string idempotencyKey,
        FeeSnapshot fee,
        DateTimeOffset now,
        Actor actor,
        string correlationId)
    {
        if (!amount.IsPositive)
        {
            throw new DomainException(Error.Validation("invalid_amount", "Payment amount must be positive."));
        }

        var payment = new Payment
        {
            Id = id,
            MerchantId = merchantId,
            Amount = amount.Amount,
            Currency = amount.Currency.Code,
            PaymentMethod = paymentMethod,
            MerchantReference = merchantReference,
            Description = description,
            Metadata = metadata,
            IdempotencyKey = idempotencyKey,
            FeePlanId = fee.FeePlanId,
            FeeRate = fee.Rate,
            FeeFixed = fee.Fixed,
            CreatedAt = now,
            UpdatedAt = now,
            Status = PaymentStatus.Created,
        };
        payment.Record(null, PaymentStatus.Created, "Payment request accepted.", now, actor, correlationId);
        payment.TransitionTo(PaymentStatus.Processing, "Sent to the simulated processor.", now, actor, correlationId);
        payment.ProcessingStartedAt = now;
        return payment;
    }

    /// <summary>Applies the processor's first answer to a processing payment.</summary>
    public void ApplyDecision(SimulatedDecision decision, DateTimeOffset now, Actor actor, string correlationId)
    {
        if (Status != PaymentStatus.Processing)
        {
            throw InvalidState("apply a processor decision");
        }

        if (decision.Outcome == SimulatedOutcome.Pending)
        {
            TransitionTo(PaymentStatus.Pending, "The simulated processor deferred the outcome.", now, actor, correlationId);
            SimulatedResolveAt = now + PaymentSimulator.PendingDelay(PaymentMethod);
            return;
        }

        ApplyFinal(decision, now, actor, correlationId);
    }

    /// <summary>Resolves a pending payment with its final outcome.</summary>
    public void ResolvePending(SimulatedDecision decision, DateTimeOffset now, Actor actor, string correlationId)
    {
        if (Status != PaymentStatus.Pending)
        {
            throw InvalidState("resolve a pending outcome");
        }

        ApplyFinal(decision, now, actor, correlationId);
    }

    public decimal RefundableAmount => Amount - RefundedAmount - PendingRefundAmount;

    /// <summary>Rules R1–R3: reserve part of the refundable balance for a new refund.</summary>
    public Result ReserveRefund(decimal amount, DateTimeOffset now)
    {
        if (Status is not (PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded))
        {
            return Error.Unprocessable("payment_not_refundable", $"Only succeeded payments can be refunded; this payment is {Status}.");
        }

        if (amount <= 0m || !Money.HasValidPrecision(amount, SharedKernel.Money.Currency.FromCode(Currency)))
        {
            return Error.Validation("invalid_amount", $"Refund amount must be positive with at most {SharedKernel.Money.Currency.FromCode(Currency).MinorUnits} decimal places for {Currency}.");
        }

        if (amount > RefundableAmount)
        {
            return Error.Unprocessable("refund_exceeds_refundable_balance",
                $"The refundable balance is {Money.Of(RefundableAmount, Currency)}; {Money.Of(amount, Currency)} was requested.");
        }

        PendingRefundAmount += amount;
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Rule R5: a reservation becomes a refund (or is given back if the refund failed).</summary>
    public void CompleteRefund(decimal amount, bool succeeded, DateTimeOffset now, Actor actor, string correlationId)
    {
        if (amount <= 0m || amount > PendingRefundAmount)
        {
            throw new DomainException(Error.Unprocessable("refund_not_reserved", $"No pending refund of {amount} {Currency} is reserved on this payment."));
        }

        PendingRefundAmount -= amount;
        UpdatedAt = now;
        if (!succeeded)
        {
            return;
        }

        RefundedAmount += amount;
        var target = RefundedAmount == Amount ? PaymentStatus.Refunded : PaymentStatus.PartiallyRefunded;
        TransitionTo(target, $"Refund of {Money.Of(amount, Currency)} succeeded.", now, actor, correlationId);
    }

    /// <summary>Rule R8: reversal only for an untouched, unsettled, succeeded payment.</summary>
    public Result Reverse(bool isSettled, string reason, DateTimeOffset now, Actor actor, string correlationId)
    {
        var blocker = this switch
        {
            { Status: not PaymentStatus.Succeeded } => $"the payment is {Status}",
            { RefundedAmount: > 0m } => "it has been refunded",
            { PendingRefundAmount: > 0m } => "a refund is in progress",
            _ when isSettled || SettlementBatchId is not null => "it has already been settled",
            _ => null,
        };
        if (blocker is not null)
        {
            return Error.Unprocessable("payment_not_reversible", $"This payment cannot be reversed because {blocker}. Use a refund instead.");
        }

        TransitionTo(PaymentStatus.Reversed, reason, now, actor, correlationId);
        return Result.Success();
    }

    private void ApplyFinal(SimulatedDecision decision, DateTimeOffset now, Actor actor, string correlationId)
    {
        switch (decision.Outcome)
        {
            case SimulatedOutcome.Succeeded:
                // Fee recognised at success (§2), from the snapshot taken at creation.
                FeeAmount = FeeCalculator.Calculate(AmountMoney, FeeRate, FeeFixed).Amount;
                TransitionTo(PaymentStatus.Succeeded, "The simulated processor approved the payment.", now, actor, correlationId);
                break;
            case SimulatedOutcome.Declined:
                Fail(PaymentStatus.Declined, decision, now, actor, correlationId);
                break;
            case SimulatedOutcome.Failed:
                Fail(PaymentStatus.Failed, decision, now, actor, correlationId);
                break;
            case SimulatedOutcome.Expired:
                Fail(PaymentStatus.Expired, decision, now, actor, correlationId);
                break;
            default:
                throw InvalidState($"apply outcome {decision.Outcome}");
        }

        CompletedAt = now;
        SimulatedResolveAt = null;
    }

    private void Fail(PaymentStatus status, SimulatedDecision decision, DateTimeOffset now, Actor actor, string correlationId)
    {
        FailureCode = decision.FailureCode ?? "unknown";
        FailureReason = decision.FailureReason ?? "No reason given.";
        TransitionTo(status, FailureReason, now, actor, correlationId);
    }

    private void TransitionTo(PaymentStatus to, string reason, DateTimeOffset now, Actor actor, string correlationId)
    {
        if (!PaymentStateMachine.CanTransition(Status, to))
        {
            throw new DomainException(Error.Unprocessable("invalid_payment_transition", $"A payment cannot move from {Status} to {to}."));
        }

        var from = Status;
        Status = to;
        UpdatedAt = now;
        Record(from, to, reason, now, actor, correlationId);
    }

    private void Record(PaymentStatus? from, PaymentStatus to, string reason, DateTimeOffset now, Actor actor, string correlationId) =>
        _history.Add(new PaymentStatusChange
        {
            PaymentId = Id,
            FromStatus = from,
            ToStatus = to,
            Reason = reason,
            ActorType = actor.Type.ToString(),
            ActorId = actor.Id,
            CorrelationId = correlationId,
            OccurredAt = now,
        });

    private DomainException InvalidState(string action) =>
        new(Error.Unprocessable("invalid_payment_state", $"Cannot {action} for a payment in status {Status}."));
}
