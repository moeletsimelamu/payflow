using PayFlow.SharedKernel.Money;
using MoneyValue = PayFlow.SharedKernel.Money.Money;

namespace PayFlow.UnitTests.Money;

public sealed class MoneyTests
{
    [Theory]
    [InlineData("10.005", "ZAR")]
    [InlineData("1.5", "JPY")]
    [InlineData("1.2345", "KWD")]
    public void Of_rejects_more_precision_than_the_currency_allows(string amount, string currency) =>
        Should.Throw<ArgumentException>(() => MoneyValue.Of(decimal.Parse(amount, System.Globalization.CultureInfo.InvariantCulture), currency));

    [Fact]
    public void Of_accepts_trailing_zeros_and_normalises_scale() =>
        MoneyValue.Of(10.500m, Currency.ZAR).ToAmountString().ShouldBe("10.50");

    // Fee examples A, B, C, E and F from docs/PAYMENT_LIFECYCLE.md section 2.
    [Theory]
    [InlineData("ZAR", "100.00", "2.00", "4.90")]
    [InlineData("ZAR", "250.00", "2.00", "9.25")]
    [InlineData("ZAR", "19.99", "2.00", "2.58")]
    [InlineData("JPY", "1000", "30", "59")]
    [InlineData("KWD", "12.345", "0.100", "0.458")]
    public void Fee_examples_match_the_documented_rules(string currency, string amount, string fixedFee, string expectedFee)
    {
        var ccy = Currency.FromCode(currency);
        MoneyValue.TryParse(amount, ccy, out var gross).ShouldBeTrue();
        MoneyValue.TryParse(fixedFee, ccy, out var fixedPart).ShouldBeTrue();

        var fee = gross.MultiplyAndRound(0.029m) + fixedPart;

        fee.ToAmountString().ShouldBe(expectedFee);
    }

    [Theory]
    [InlineData("2.50", "0.05", "0.13")]
    [InlineData("-2.50", "0.05", "-0.13")]
    public void Rounding_is_half_away_from_zero(string amount, string rate, string expected)
    {
        MoneyValue.TryParse(amount, Currency.ZAR, out var value).ShouldBeTrue();
        value.MultiplyAndRound(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)).ToAmountString().ShouldBe(expected);
    }

    [Fact]
    public void Arithmetic_across_currencies_throws()
    {
        var zar = MoneyValue.Of(1m, Currency.ZAR);
        var usd = MoneyValue.Of(1m, Currency.USD);
        Should.Throw<CurrencyMismatchException>(() => zar + usd);
        Should.Throw<CurrencyMismatchException>(() => zar.CompareTo(usd));
    }

    [Theory]
    [InlineData("100.00", true)]
    [InlineData("-5.00", true)]
    [InlineData("1e3", false)]
    [InlineData(" 1.00", false)]
    [InlineData("1,000.00", false)]
    [InlineData("+5", false)]
    [InlineData("10.001", false)]
    [InlineData("", false)]
    public void TryParse_accepts_only_plain_decimal_strings(string input, bool expected) =>
        MoneyValue.TryParse(input, Currency.ZAR, out _).ShouldBe(expected);

    [Fact]
    public void Default_money_cannot_be_used()
    {
        MoneyValue uninitialised = default;
        Should.Throw<InvalidOperationException>(() => uninitialised.ToAmountString());
        Should.Throw<InvalidOperationException>(() => uninitialised + MoneyValue.Of(1m, Currency.ZAR));
    }

    [Fact]
    public void Settlement_worked_example_nets_to_313_26()
    {
        var zar = Currency.ZAR;
        var lines = new[] { 95.10m, 240.75m, 17.41m, -40.00m }.Select(a => MoneyValue.Of(a, zar));
        MoneyValue.Sum(lines, zar).ToAmountString().ShouldBe("313.26");
    }
}
