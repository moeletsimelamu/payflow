using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Settlement.Domain;

public enum SettlementStatus
{
    Pending,
    Paid,
    Failed,
}

public enum SettlementItemType
{
    Payment,
    Refund,
}

/// <summary>One payment (positive) or refund (negative) included in a batch. Unique per source: an item can settle once (S5).</summary>
internal sealed class SettlementItem
{
    public long Id { get; init; }

    public Guid BatchId { get; init; }

    public SettlementItemType ItemType { get; init; }

    public Guid SourceId { get; init; }

    public Guid PaymentId { get; init; }

    public decimal GrossAmount { get; init; }

    public decimal FeeAmount { get; init; }

    /// <summary>Signed: payment gross − fee, or −refund.</summary>
    public decimal NetAmount { get; init; }

    public DateTimeOffset CompletedAt { get; init; }
}

/// <summary>
/// A settlement batch for one merchant and currency (docs/PAYMENT_LIFECYCLE.md §6).
/// <code>
/// net    = Σ payment gross − Σ fees − Σ refunds + carried_forward_in   (S3)
/// payout = max(net, 0);  carried_forward_out = min(net, 0)             (S4)
/// </code>
/// Composition happens once; afterwards only the payout outcome changes. A batch with nothing to
/// pay (net ≤ 0) is complete on creation; its deficit carries into the next batch.
/// </summary>
internal sealed class SettlementBatch
{
    public const string NoPayoutReference = "NO-PAYOUT";

    private readonly List<SettlementItem> _items = [];

    private SettlementBatch()
    {
    }

    public Guid Id { get; private init; }

    public Guid MerchantId { get; private init; }

    public string Currency { get; private init; } = string.Empty;

    public DateTimeOffset CutoffAt { get; private init; }

    public SettlementStatus Status { get; private set; }

    public int PaymentCount { get; private init; }

    public int RefundCount { get; private init; }

    public decimal GrossPayments { get; private init; }

    public decimal TotalFees { get; private init; }

    public decimal TotalRefunds { get; private init; }

    public decimal CarriedForwardIn { get; private init; }

    public decimal NetAmount { get; private init; }

    public decimal PayoutAmount { get; private init; }

    public decimal CarriedForwardOut { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public string CreatedBy { get; private init; } = string.Empty;

    public DateTimeOffset? PaidAt { get; private set; }

    public string? PayoutReference { get; private set; }

    public string? FailureReason { get; private set; }

    public int PayoutAttempts { get; private set; }

    public uint Version { get; private set; }

    public IReadOnlyList<SettlementItem> Items => _items;

    public static SettlementBatch Compose(
        Guid id,
        Guid merchantId,
        string currency,
        DateTimeOffset cutoff,
        decimal carriedForwardIn,
        IReadOnlyCollection<SettleablePayment> payments,
        IReadOnlyCollection<SettleableRefund> refunds,
        string createdBy,
        DateTimeOffset now)
    {
        if (payments.Count + refunds.Count == 0)
        {
            throw new DomainException(Error.Unprocessable("nothing_to_settle", "A batch needs at least one payment or refund (S7)."));
        }

        if (carriedForwardIn > 0m)
        {
            throw new DomainException(Error.Unprocessable("invalid_carry_forward", "Only a deficit (≤ 0) can be carried forward."));
        }

        var ccy = SharedKernel.Money.Currency.FromCode(currency);
        var gross = Money.Sum(payments.Select(p => Money.Of(p.Amount, ccy)), ccy);
        var fees = Money.Sum(payments.Select(p => Money.Of(p.Fee, ccy)), ccy);
        var refunded = Money.Sum(refunds.Select(r => Money.Of(r.Amount, ccy)), ccy);
        var net = gross - fees - refunded + Money.Of(carriedForwardIn, ccy);
        var payout = Money.Max(net, Money.Zero(ccy));
        var carryOut = Money.Min(net, Money.Zero(ccy));

        var batch = new SettlementBatch
        {
            Id = id,
            MerchantId = merchantId,
            Currency = currency,
            CutoffAt = cutoff,
            Status = SettlementStatus.Pending,
            PaymentCount = payments.Count,
            RefundCount = refunds.Count,
            GrossPayments = gross.Amount,
            TotalFees = fees.Amount,
            TotalRefunds = refunded.Amount,
            CarriedForwardIn = carriedForwardIn,
            NetAmount = net.Amount,
            PayoutAmount = payout.Amount,
            CarriedForwardOut = carryOut.Amount,
            CreatedAt = now,
            CreatedBy = createdBy,
        };

        foreach (var p in payments)
        {
            batch._items.Add(new SettlementItem
            {
                BatchId = id,
                ItemType = SettlementItemType.Payment,
                SourceId = p.PaymentId,
                PaymentId = p.PaymentId,
                GrossAmount = p.Amount,
                FeeAmount = p.Fee,
                NetAmount = (Money.Of(p.Amount, ccy) - Money.Of(p.Fee, ccy)).Amount,
                CompletedAt = p.CompletedAt,
            });
        }

        foreach (var r in refunds)
        {
            batch._items.Add(new SettlementItem
            {
                BatchId = id,
                ItemType = SettlementItemType.Refund,
                SourceId = r.RefundId,
                PaymentId = r.PaymentId,
                GrossAmount = -r.Amount,
                FeeAmount = 0m,
                NetAmount = -r.Amount,
                CompletedAt = r.CompletedAt,
            });
        }

        if (!payout.IsPositive)
        {
            // Nothing to send: complete immediately; the deficit (if any) carries forward.
            batch.Status = SettlementStatus.Paid;
            batch.PaidAt = now;
            batch.PayoutReference = NoPayoutReference;
        }

        return batch;
    }

    /// <summary>Rule S6: Pending or Failed → Paid. A failed batch is retried as-is, never re-composed.</summary>
    public Result MarkPaid(string payoutReference, DateTimeOffset now)
    {
        if (Status == SettlementStatus.Paid)
        {
            return Error.Conflict("settlement_already_paid", "This settlement batch is already paid.");
        }

        PayoutAttempts++;
        Status = SettlementStatus.Paid;
        PaidAt = now;
        PayoutReference = payoutReference;
        FailureReason = null;
        return Result.Success();
    }

    public Result MarkFailed(string reason, DateTimeOffset now)
    {
        if (Status == SettlementStatus.Paid)
        {
            return Error.Conflict("settlement_already_paid", "This settlement batch is already paid.");
        }

        PayoutAttempts++;
        Status = SettlementStatus.Failed;
        FailureReason = $"{reason} ({now:u})";
        return Result.Success();
    }
}
