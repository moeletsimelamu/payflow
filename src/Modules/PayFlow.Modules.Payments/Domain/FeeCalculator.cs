using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Payments.Domain;

/// <summary>
/// Fee rule (docs/PAYMENT_LIFECYCLE.md §2):
/// <c>fee = round_half_up(amount × rate, minor units) + fixed fee</c>, with <c>0 ≤ fee &lt; amount</c>.
/// Rounding happens exactly once, here; the result is stored and never recomputed.
/// </summary>
internal static class FeeCalculator
{
    public static Money Calculate(Money amount, decimal percentageRate, decimal fixedFee)
    {
        var fee = amount.MultiplyAndRound(percentageRate) + Money.Of(fixedFee, amount.Currency);
        if (fee.IsNegative || fee >= amount)
        {
            throw new DomainException(Error.Unprocessable("fee_exceeds_amount", $"Fee {fee} would not be less than the amount {amount}."));
        }

        return fee;
    }
}
