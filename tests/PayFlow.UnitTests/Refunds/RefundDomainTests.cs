using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Domain;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;
using MoneyValue = PayFlow.SharedKernel.Money.Money;

namespace PayFlow.UnitTests.Refunds;

public sealed class RefundDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Actor Merchant = new(ActorType.ApiKey, "key_test", "api_key:test");

    [Fact]
    public void Partial_refunds_reserve_then_complete_and_the_last_one_marks_the_payment_refunded()
    {
        var payment = Succeeded(100.00m);

        payment.ReserveRefund(40.00m, Now).IsSuccess.ShouldBeTrue();
        payment.RefundableAmount.ShouldBe(60.00m);
        payment.Status.ShouldBe(PaymentStatus.Succeeded); // status only moves when the refund succeeds

        payment.CompleteRefund(40.00m, succeeded: true, Now, Merchant, "c");
        payment.Status.ShouldBe(PaymentStatus.PartiallyRefunded);
        payment.RefundedAmount.ShouldBe(40.00m);
        payment.PendingRefundAmount.ShouldBe(0m);

        payment.ReserveRefund(60.00m, Now).IsSuccess.ShouldBeTrue();
        payment.CompleteRefund(60.00m, succeeded: true, Now, Merchant, "c");
        payment.Status.ShouldBe(PaymentStatus.Refunded);
        payment.RefundableAmount.ShouldBe(0m);
    }

    [Fact]
    public void A_failed_refund_gives_the_reservation_back()
    {
        var payment = Succeeded(100.00m);
        payment.ReserveRefund(100.00m, Now).IsSuccess.ShouldBeTrue();

        payment.CompleteRefund(100.00m, succeeded: false, Now, Merchant, "c");

        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        payment.RefundableAmount.ShouldBe(100.00m);
        payment.RefundedAmount.ShouldBe(0m);
    }

    [Theory]
    [InlineData("100.01", "refund_exceeds_refundable_balance")]
    [InlineData("0", "invalid_amount")]
    [InlineData("-1.00", "invalid_amount")]
    [InlineData("10.005", "invalid_amount")]
    public void Refunds_cannot_exceed_the_balance_or_currency_precision(string amount, string code)
    {
        var payment = Succeeded(100.00m);

        payment.ReserveRefund(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), Now).Error!.Code.ShouldBe(code);
        payment.RefundableAmount.ShouldBe(100.00m);
    }

    [Fact]
    public void Pending_reservations_count_against_the_balance()
    {
        var payment = Succeeded(100.00m);
        payment.ReserveRefund(70.00m, Now).IsSuccess.ShouldBeTrue();

        payment.ReserveRefund(30.01m, Now).Error!.Code.ShouldBe("refund_exceeds_refundable_balance");
        payment.ReserveRefund(30.00m, Now).IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("sim_card_declined_insufficient_funds")]
    [InlineData("sim_card_processor_error")]
    [InlineData("sim_card_pending")]
    public void Only_captured_payments_can_be_refunded(string method)
    {
        var payment = NewPayment(method, 100.00m);
        payment.ApplyDecision(PaymentSimulator.Decide(method), Now, Merchant, "c");

        payment.ReserveRefund(10.00m, Now).Error!.Code.ShouldBe("payment_not_refundable");
    }

    [Fact]
    public void Completing_more_than_was_reserved_is_a_programming_error() =>
        Should.Throw<DomainException>(() => Succeeded(100.00m).CompleteRefund(10.00m, succeeded: true, Now, Merchant, "c"))
            .Error.Code.ShouldBe("refund_not_reserved");

    public static TheoryData<string, bool, string?> ReversalMatrix() => new()
    {
        { "clean", false, null },
        { "settled", true, "payment_not_reversible" },
        { "partially_refunded", false, "payment_not_reversible" },
        { "refund_pending", false, "payment_not_reversible" },
        { "fully_refunded", false, "payment_not_reversible" },
        { "declined", false, "payment_not_reversible" },
        { "already_reversed", false, "payment_not_reversible" },
    };

    [Theory]
    [MemberData(nameof(ReversalMatrix))]
    public void Reversal_eligibility_matrix(string state, bool settled, string? expectedError)
    {
        var payment = state == "declined" ? Declined() : Succeeded(100.00m);
        switch (state)
        {
            case "partially_refunded":
                payment.ReserveRefund(10.00m, Now);
                payment.CompleteRefund(10.00m, succeeded: true, Now, Merchant, "c");
                break;
            case "refund_pending":
                payment.ReserveRefund(10.00m, Now);
                break;
            case "fully_refunded":
                payment.ReserveRefund(100.00m, Now);
                payment.CompleteRefund(100.00m, succeeded: true, Now, Merchant, "c");
                break;
            case "already_reversed":
                payment.Reverse(false, "first", Now, Merchant, "c").IsSuccess.ShouldBeTrue();
                break;
        }

        var result = payment.Reverse(settled, "customer cancelled", Now, Merchant, "c");

        if (expectedError is null)
        {
            result.IsSuccess.ShouldBeTrue();
            payment.Status.ShouldBe(PaymentStatus.Reversed);
            payment.ReserveRefund(1.00m, Now).Error!.Code.ShouldBe("payment_not_refundable");
        }
        else
        {
            result.Error!.Code.ShouldBe(expectedError);
        }
    }

    [Fact]
    public void A_refund_resolves_exactly_once()
    {
        var refund = Refund.Request(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), 10.00m, "ZAR", null, "sim_card_success", "k", Merchant, Now);
        refund.Status.ShouldBe(RefundStatus.Pending);

        refund.Succeed(Now);

        refund.Status.ShouldBe(RefundStatus.Succeeded);
        Should.Throw<DomainException>(() => refund.Fail("late", Now)).Error.Code.ShouldBe("refund_not_pending");
    }

    [Theory]
    [InlineData("sim_card_success", true)]
    [InlineData("sim_card_pending", true)]
    [InlineData("sim_card_refund_fails", false)]
    public void Refund_outcomes_are_simulated_from_the_payment_method(string method, bool succeeds) =>
        RefundSimulator.Succeeds(method).ShouldBe(succeeds);

    private static Payment Succeeded(decimal amount)
    {
        var payment = NewPayment("sim_card_success", amount);
        payment.ApplyDecision(SimulatedDecision.Success, Now, Merchant, "c");
        return payment;
    }

    private static Payment Declined()
    {
        var payment = NewPayment("sim_card_declined_do_not_honour", 100.00m);
        payment.ApplyDecision(PaymentSimulator.Decide(payment.PaymentMethod), Now, Merchant, "c");
        return payment;
    }

    private static Payment NewPayment(string method, decimal amount) => Payment.Initiate(
        Guid.CreateVersion7(), Guid.CreateVersion7(), MoneyValue.Of(amount, Currency.ZAR), method, "order", null, null, "key",
        new FeeSnapshot(Guid.CreateVersion7(), 0.029m, 2.00m), Now, Merchant, "c");
}
