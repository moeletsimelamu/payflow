using System.Text.Json;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Ledger.Domain;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;
using MoneyValue = PayFlow.SharedKernel.Money.Money;

namespace PayFlow.UnitTests.Payments;

public sealed class PaymentDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly Actor Merchant = new(ActorType.ApiKey, "key_test", "api_key:test");

    /// <summary>The documented state machine (docs/PAYMENT_LIFECYCLE.md §3), written out independently of the code.</summary>
    private static readonly HashSet<(PaymentStatus, PaymentStatus)> Documented =
    [
        (PaymentStatus.Created, PaymentStatus.Processing),
        (PaymentStatus.Processing, PaymentStatus.Succeeded),
        (PaymentStatus.Processing, PaymentStatus.Declined),
        (PaymentStatus.Processing, PaymentStatus.Failed),
        (PaymentStatus.Processing, PaymentStatus.Pending),
        (PaymentStatus.Pending, PaymentStatus.Succeeded),
        (PaymentStatus.Pending, PaymentStatus.Declined),
        (PaymentStatus.Pending, PaymentStatus.Failed),
        (PaymentStatus.Pending, PaymentStatus.Expired),
        (PaymentStatus.Succeeded, PaymentStatus.PartiallyRefunded),
        (PaymentStatus.Succeeded, PaymentStatus.Refunded),
        (PaymentStatus.Succeeded, PaymentStatus.Reversed),
        (PaymentStatus.PartiallyRefunded, PaymentStatus.PartiallyRefunded),
        (PaymentStatus.PartiallyRefunded, PaymentStatus.Refunded),
    ];

    public static TheoryData<PaymentStatus, PaymentStatus> AllPairs()
    {
        var data = new TheoryData<PaymentStatus, PaymentStatus>();
        foreach (var from in Enum.GetValues<PaymentStatus>())
        {
            foreach (var to in Enum.GetValues<PaymentStatus>())
            {
                data.Add(from, to);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(AllPairs))]
    public void Every_status_pair_matches_the_documented_state_machine(PaymentStatus from, PaymentStatus to) =>
        PaymentStateMachine.CanTransition(from, to).ShouldBe(Documented.Contains((from, to)), $"{from} → {to}");

    // Fee examples A, B, C, E, F from docs/PAYMENT_LIFECYCLE.md §2, at 2.9 %.
    [Theory]
    [InlineData("100.00", "ZAR", "2.00", "4.90")]
    [InlineData("250.00", "ZAR", "2.00", "9.25")]
    [InlineData("19.99", "ZAR", "2.00", "2.58")]
    [InlineData("1000", "JPY", "30", "59")]
    [InlineData("12.345", "KWD", "0.100", "0.458")]
    public void Fee_calculator_reproduces_the_documented_examples(string amount, string currency, string fixedFee, string expected)
    {
        var ccy = Currency.FromCode(currency);
        MoneyValue.TryParse(amount, ccy, out var gross).ShouldBeTrue();

        FeeCalculator.Calculate(gross, 0.029m, decimal.Parse(fixedFee, System.Globalization.CultureInfo.InvariantCulture))
            .ToAmountString().ShouldBe(expected);
    }

    [Fact]
    public void A_fee_can_never_consume_the_whole_payment() =>
        Should.Throw<DomainException>(() => FeeCalculator.Calculate(MoneyValue.Of(2.00m, Currency.ZAR), 0.029m, 2.00m))
            .Error.Code.ShouldBe("fee_exceeds_amount");

    [Fact]
    public void A_successful_payment_records_its_lifecycle_and_fee_from_the_snapshot()
    {
        var payment = NewPayment("sim_card_success", 100.00m);
        payment.Status.ShouldBe(PaymentStatus.Processing);

        payment.ApplyDecision(PaymentSimulator.Decide(payment.PaymentMethod), Now.AddMilliseconds(40), Merchant, "corr");

        payment.Status.ShouldBe(PaymentStatus.Succeeded);
        payment.FeeAmount.ShouldBe(4.90m);
        payment.CompletedAt.ShouldBe(Now.AddMilliseconds(40));
        payment.History.Select(h => h.ToStatus).ShouldBe([PaymentStatus.Created, PaymentStatus.Processing, PaymentStatus.Succeeded]);
        payment.History[0].FromStatus.ShouldBeNull();
    }

    [Theory]
    [InlineData("sim_card_declined_insufficient_funds", PaymentStatus.Declined, "insufficient_funds")]
    [InlineData("sim_card_declined_do_not_honour", PaymentStatus.Declined, "do_not_honour")]
    [InlineData("sim_card_processor_error", PaymentStatus.Failed, "processor_error")]
    public void Unsuccessful_outcomes_carry_a_failure_code_and_no_fee(string method, PaymentStatus expected, string code)
    {
        var payment = NewPayment(method, 50.00m);

        payment.ApplyDecision(PaymentSimulator.Decide(method), Now, Merchant, "corr");

        payment.Status.ShouldBe(expected);
        payment.FailureCode.ShouldBe(code);
        payment.FeeAmount.ShouldBeNull();
    }

    [Theory]
    [InlineData("sim_card_pending", PaymentStatus.Succeeded, 10)]
    [InlineData("sim_card_pending_then_declined", PaymentStatus.Declined, 10)]
    [InlineData("sim_card_pending_expires", PaymentStatus.Expired, 1800)]
    public void Pending_payments_resolve_to_the_simulated_final_outcome(string method, PaymentStatus final, int delaySeconds)
    {
        var payment = NewPayment(method, 50.00m);
        payment.ApplyDecision(PaymentSimulator.Decide(method), Now, Merchant, "corr");
        payment.Status.ShouldBe(PaymentStatus.Pending);
        payment.SimulatedResolveAt.ShouldBe(Now.AddSeconds(delaySeconds));

        payment.ResolvePending(PaymentSimulator.ResolvePending(method), Now.AddSeconds(delaySeconds), Actor.System("test"), "corr");

        payment.Status.ShouldBe(final);
        payment.SimulatedResolveAt.ShouldBeNull();
    }

    [Fact]
    public void Illegal_operations_are_rejected()
    {
        var payment = NewPayment("sim_card_success", 10.00m);
        payment.ApplyDecision(SimulatedDecision.Success, Now, Merchant, "corr");

        Should.Throw<DomainException>(() => payment.ApplyDecision(SimulatedDecision.Success, Now, Merchant, "corr")).Error.Code.ShouldBe("invalid_payment_state");
        Should.Throw<DomainException>(() => payment.ResolvePending(SimulatedDecision.Success, Now, Merchant, "corr")).Error.Code.ShouldBe("invalid_payment_state");
    }

    [Theory]
    [InlineData("4111 1111 1111 1111", true)]
    [InlineData("order for card 4111-1111-1111-1111 please", true)]
    [InlineData("5500005555555559", true)]
    [InlineData("4111111111111112", false)] // fails Luhn
    [InlineData("order-2026-11-12-000123", false)]
    [InlineData("123456789012", false)] // 12 digits
    [InlineData("sim_card_success", false)]
    [InlineData(null, false)]
    public void Card_numbers_are_detected_anywhere_in_text(string? text, bool expected) =>
        CardDataDetector.ContainsCardNumber(text).ShouldBe(expected);

    [Fact]
    public void Idempotency_hash_ignores_key_order_but_not_values()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var a = IdempotencyKeys.HashRequest("POST", "/v1/payments", new { amount = "10.00", currency = "ZAR", metadata = new { b = "2", a = "1" } }, options);
        var reordered = IdempotencyKeys.HashRequest("POST", "/v1/payments", new { currency = "ZAR", metadata = new { a = "1", b = "2" }, amount = "10.00" }, options);
        var different = IdempotencyKeys.HashRequest("POST", "/v1/payments", new { amount = "10.01", currency = "ZAR", metadata = new { a = "1", b = "2" } }, options);

        a.ShouldBe(reordered);
        a.ShouldNotBe(different);
    }

    [Theory]
    [InlineData(null, "idempotency_key_required")]
    [InlineData("", "idempotency_key_required")]
    [InlineData("has space", "idempotency_key_invalid")]
    [InlineData("ünïcode", "idempotency_key_invalid")]
    public void Invalid_idempotency_keys_are_rejected(string? key, string code) =>
        IdempotencyKeys.Validate(key).Error!.Code.ShouldBe(code);

    [Fact]
    public void Ledger_rules_reject_unbalanced_or_malformed_entries()
    {
        var merchant = Guid.CreateVersion7();
        JournalEntryRequest Entry(params PostingRequest[] postings) => new("Test", "test", Guid.CreateVersion7(), "ZAR", "test", postings);

        JournalRules.Validate(Entry(
            new(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Debit, 10.00m),
            new(LedgerAccountKind.MerchantPayable, merchant, PostingDirection.Credit, 10.00m))).IsSuccess.ShouldBeTrue();

        JournalRules.Validate(Entry(
            new(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Debit, 10.00m),
            new(LedgerAccountKind.MerchantPayable, merchant, PostingDirection.Credit, 9.99m))).Error!.Code.ShouldBe("ledger_unbalanced");

        JournalRules.Validate(Entry(
            new(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Debit, 10.005m),
            new(LedgerAccountKind.MerchantPayable, merchant, PostingDirection.Credit, 10.005m))).Error!.Code.ShouldBe("ledger_invalid_amount");

        JournalRules.Validate(Entry(
            new(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Debit, 10.00m),
            new(LedgerAccountKind.MerchantPayable, null, PostingDirection.Credit, 10.00m))).Error!.Code.ShouldBe("ledger_invalid_account");

        JournalRules.Validate(Entry(new PostingRequest(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Debit, 10.00m))).Error!.Code.ShouldBe("ledger_too_few_postings");
    }

    private static Payment NewPayment(string method, decimal amount) => Payment.Initiate(
        Guid.CreateVersion7(),
        Guid.CreateVersion7(),
        MoneyValue.Of(amount, Currency.ZAR),
        method,
        "order-1",
        null,
        null,
        "key-1",
        new FeeSnapshot(Guid.CreateVersion7(), 0.029m, 2.00m),
        Now,
        Merchant,
        "corr");
}
