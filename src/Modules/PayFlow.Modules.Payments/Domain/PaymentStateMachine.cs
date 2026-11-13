using PayFlow.Modules.Payments.Contracts;

namespace PayFlow.Modules.Payments.Domain;

/// <summary>
/// The only legal payment transitions (docs/PAYMENT_LIFECYCLE.md §3). Anything not listed is
/// rejected. Refund and reversal transitions are listed now and used from Phase 5.
/// </summary>
internal static class PaymentStateMachine
{
    private static readonly Dictionary<PaymentStatus, PaymentStatus[]> Allowed = new()
    {
        [PaymentStatus.Created] = [PaymentStatus.Processing],
        [PaymentStatus.Processing] = [PaymentStatus.Succeeded, PaymentStatus.Declined, PaymentStatus.Failed, PaymentStatus.Pending],
        [PaymentStatus.Pending] = [PaymentStatus.Succeeded, PaymentStatus.Declined, PaymentStatus.Failed, PaymentStatus.Expired],
        [PaymentStatus.Succeeded] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded, PaymentStatus.Reversed],
        [PaymentStatus.PartiallyRefunded] = [PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded],
        [PaymentStatus.Declined] = [],
        [PaymentStatus.Failed] = [],
        [PaymentStatus.Expired] = [],
        [PaymentStatus.Refunded] = [],
        [PaymentStatus.Reversed] = [],
    };

    public static bool CanTransition(PaymentStatus from, PaymentStatus to) => Allowed[from].Contains(to);

    public static bool IsTerminal(PaymentStatus status) => Allowed[status].Length == 0;

    /// <summary>Statuses in which the payment has captured funds and carries a fee.</summary>
    public static bool IsCaptured(PaymentStatus status) =>
        status is PaymentStatus.Succeeded or PaymentStatus.PartiallyRefunded or PaymentStatus.Refunded or PaymentStatus.Reversed;
}
