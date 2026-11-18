using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Settlement.Domain;
using PayFlow.SharedKernel.Results;

namespace PayFlow.UnitTests.Settlement;

public sealed class SettlementComposeTests
{
    private static readonly DateTimeOffset Cutoff = new(2026, 11, 18, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Earlier = Cutoff.AddHours(-6);
    private static readonly Guid Merchant = Guid.CreateVersion7();

    [Fact]
    public void The_documented_worked_example_settles_to_ZAR_313_26()
    {
        var a = Guid.CreateVersion7();
        var batch = Compose(
            0m,
            [Payment(100.00m, 4.90m, a), Payment(250.00m, 9.25m), Payment(19.99m, 2.58m)],
            [new SettleableRefund(Guid.CreateVersion7(), a, 40.00m, Earlier)]);

        batch.GrossPayments.ShouldBe(369.99m);
        batch.TotalFees.ShouldBe(16.73m);
        batch.TotalRefunds.ShouldBe(40.00m);
        batch.NetAmount.ShouldBe(313.26m);
        batch.PayoutAmount.ShouldBe(313.26m);
        batch.CarriedForwardOut.ShouldBe(0m);
        batch.Status.ShouldBe(SettlementStatus.Pending);
        batch.PaymentCount.ShouldBe(3);
        batch.RefundCount.ShouldBe(1);
        batch.Items.Sum(i => i.NetAmount).ShouldBe(313.26m);
        batch.Items.Single(i => i.ItemType == SettlementItemType.Refund).NetAmount.ShouldBe(-40.00m);
    }

    [Fact]
    public void A_deficit_pays_nothing_and_is_carried_forward()
    {
        var batch = Compose(0m, [], [new SettleableRefund(Guid.CreateVersion7(), Guid.CreateVersion7(), 100.00m, Earlier)]);

        batch.NetAmount.ShouldBe(-100.00m);
        batch.PayoutAmount.ShouldBe(0m);
        batch.CarriedForwardOut.ShouldBe(-100.00m);
        batch.Status.ShouldBe(SettlementStatus.Paid); // nothing to pay out
        batch.PayoutReference.ShouldBe(SettlementBatch.NoPayoutReference);
    }

    [Fact]
    public void A_carried_deficit_is_netted_by_the_next_batch()
    {
        var batch = Compose(-100.00m, [Payment(150.00m, 6.35m)], []);

        batch.CarriedForwardIn.ShouldBe(-100.00m);
        batch.NetAmount.ShouldBe(43.65m);
        batch.PayoutAmount.ShouldBe(43.65m);
        batch.CarriedForwardOut.ShouldBe(0m);
    }

    [Fact]
    public void A_deficit_larger_than_new_sales_keeps_rolling()
    {
        var batch = Compose(-100.00m, [Payment(50.00m, 3.45m)], []);

        batch.NetAmount.ShouldBe(-53.45m);
        batch.PayoutAmount.ShouldBe(0m);
        batch.CarriedForwardOut.ShouldBe(-53.45m);
    }

    [Fact]
    public void Empty_batches_and_positive_carry_forwards_are_refused()
    {
        Should.Throw<DomainException>(() => Compose(0m, [], [])).Error.Code.ShouldBe("nothing_to_settle");
        Should.Throw<DomainException>(() => Compose(10m, [Payment(10m, 2.29m)], [])).Error.Code.ShouldBe("invalid_carry_forward");
    }

    [Fact]
    public void A_failed_payout_can_be_retried_but_a_paid_batch_cannot_change()
    {
        var batch = Compose(0m, [Payment(100.00m, 4.90m)], []);

        batch.MarkFailed("bank said no", Cutoff).IsSuccess.ShouldBeTrue();
        batch.Status.ShouldBe(SettlementStatus.Failed);
        batch.MarkPaid("SIM-1", Cutoff).IsSuccess.ShouldBeTrue();
        batch.Status.ShouldBe(SettlementStatus.Paid);
        batch.FailureReason.ShouldBeNull();

        batch.MarkPaid("SIM-2", Cutoff).Error!.Code.ShouldBe("settlement_already_paid");
        batch.MarkFailed("late", Cutoff).Error!.Code.ShouldBe("settlement_already_paid");
        batch.PayoutReference.ShouldBe("SIM-1");
    }

    private static SettleablePayment Payment(decimal amount, decimal fee, Guid? id = null) => new(id ?? Guid.CreateVersion7(), amount, fee, Earlier);

    private static SettlementBatch Compose(decimal carryIn, SettleablePayment[] payments, SettleableRefund[] refunds) =>
        SettlementBatch.Compose(Guid.CreateVersion7(), Merchant, "ZAR", Cutoff, carryIn, payments, refunds, "test", Cutoff);
}
