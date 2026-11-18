using PayFlow.Modules.Reporting.Application;

namespace PayFlow.UnitTests.Reporting;

public sealed class ReportingQueryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 18, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Defaults_to_the_last_30_days_in_ZAR()
    {
        var q = ReportingService.Validate(null, null, null, null, includeReconciliation: false, Now).Value;

        q.Currency.ShouldBe("ZAR");
        q.To.ShouldBe(new DateOnly(2026, 11, 18));
        q.From.ShouldBe(new DateOnly(2026, 10, 20));
    }

    [Theory]
    [InlineData("XYZ", "2026-11-01", "2026-11-18", "unsupported_currency")]
    [InlineData("ZAR", "2026-11-18", "2026-11-01", "invalid_period")]
    [InlineData("ZAR", "2025-01-01", "2026-11-18", "invalid_period")]
    public void Bad_queries_are_rejected(string currency, string from, string to, string code) =>
        ReportingService.Validate(null, currency, DateOnly.Parse(from, System.Globalization.CultureInfo.InvariantCulture), DateOnly.Parse(to, System.Globalization.CultureInfo.InvariantCulture), false, Now)
            .Error!.Code.ShouldBe(code);

    [Fact]
    public void Currency_codes_are_normalised_and_a_full_year_is_allowed()
    {
        var q = ReportingService.Validate(null, " usd ", new DateOnly(2025, 11, 18), new DateOnly(2026, 11, 18), false, Now).Value;

        q.Currency.ShouldBe("USD");
        (q.To.DayNumber - q.From.DayNumber + 1).ShouldBe(366);
    }

    [Theory]
    [InlineData(4, 7, 0.5714)]
    [InlineData(1, 7, 0.1429)]
    [InlineData(0, 0, 0)]
    [InlineData(40, 411.99, 0.0971)]
    public void Rates_are_fractions_rounded_to_four_places(decimal part, decimal whole, decimal expected) =>
        ReportingService.Rate(part, whole).ShouldBe(expected);

    [Fact]
    public void Cache_keys_separate_scope_currency_window_and_reconciliation()
    {
        var merchant = Guid.CreateVersion7();
        var a = new ReportQuery(merchant, "ZAR", new DateOnly(2026, 11, 1), new DateOnly(2026, 11, 18), false);

        var keys = new[]
        {
            ReportingService.CacheKey(a),
            ReportingService.CacheKey(a with { MerchantId = null }),
            ReportingService.CacheKey(a with { Currency = "USD" }),
            ReportingService.CacheKey(a with { To = new DateOnly(2026, 11, 17) }),
            ReportingService.CacheKey(a with { IncludeReconciliation = true }),
        };

        keys.Distinct().Count().ShouldBe(keys.Length);
        ReportingService.CacheKey(a).ShouldBe(ReportingService.CacheKey(a with { }));
    }
}
