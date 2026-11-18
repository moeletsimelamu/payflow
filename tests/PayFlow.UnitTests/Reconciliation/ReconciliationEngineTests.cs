using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Reconciliation.Application;
using PayFlow.Modules.Reconciliation.Domain;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.SharedKernel.Ids;

namespace PayFlow.UnitTests.Reconciliation;

public sealed class ReconciliationEngineTests
{
    private static readonly DateTimeOffset Completed = new(2026, 11, 17, 10, 0, 0, TimeSpan.Zero);
    private static readonly Guid Merchant = Guid.CreateVersion7();

    [Fact]
    public void A_line_that_agrees_on_currency_amount_and_status_matches()
    {
        var p = Payment(100.00m);
        var results = Match([Line(1, p, "CAPTURED")], [p]);

        results.Single().Outcome.ShouldBe(ReconciliationOutcome.Matched);
    }

    [Fact]
    public void Currency_mismatch_wins_over_amount_and_status()
    {
        var p = Payment(100.00m);
        var line = Line(1, p, "REVERSED", amount: 101.00m, currency: "USD");

        Match([line], [p]).Single().Outcome.ShouldBe(ReconciliationOutcome.CurrencyMismatch);
    }

    [Fact]
    public void Amount_mismatch_has_zero_tolerance_and_wins_over_status()
    {
        var p = Payment(100.00m);

        Match([Line(1, p, "REVERSED", amount: 100.01m)], [p]).Single().Outcome.ShouldBe(ReconciliationOutcome.AmountMismatch);
        Match([Line(1, p, "REVERSED")], [p]).Single().Outcome.ShouldBe(ReconciliationOutcome.StatusDiscrepancy);
    }

    [Fact]
    public void Details_show_amounts_in_the_currency_precision_not_the_stored_scale()
    {
        var p = Payment(250.0000m); // as loaded from numeric(19,4)
        var detail = Match([Line(1, p, "CAPTURED", amount: 251.00m)], [p]).Single().Detail;

        detail.ShouldBe("PayFlow recorded 250.00 ZAR; the statement says 251.00 ZAR (difference 1.00).");
        ReconciliationEngine.Format(100m, "JPY").ShouldBe("100");
        ReconciliationEngine.Format(1.5m, "KWD").ShouldBe("1.500");
    }

    [Fact]
    public void Unknown_or_malformed_references_are_missing_internal()
    {
        var lines = new[]
        {
            Line(1, PublicId.Create(IdPrefixes.Payment, Guid.CreateVersion7()), StatementTransactionType.PAYMENT, 10m, "ZAR", "CAPTURED"),
            Line(2, "order-123", StatementTransactionType.PAYMENT, 10m, "ZAR", "CAPTURED"),
            Line(3, PublicId.Create(IdPrefixes.Payment, Guid.CreateVersion7()), StatementTransactionType.REFUND, 10m, "ZAR", "REFUNDED"), // wrong prefix for a refund
        };

        Match(lines, []).ShouldAllBe(r => r.Outcome == ReconciliationOutcome.MissingInternal);
    }

    [Fact]
    public void Repeated_lines_are_duplicates_and_the_first_is_still_evaluated()
    {
        var p = Payment(100.00m);
        var results = Match([Line(5, p, "CAPTURED"), Line(2, p, "CAPTURED"), Line(9, p, "CAPTURED")], [p]);

        results.Count.ShouldBe(3);
        results.Single(r => r.Line!.LineNumber == 2).Outcome.ShouldBe(ReconciliationOutcome.Matched); // file order, not list order
        results.Where(r => r.Outcome == ReconciliationOutcome.DuplicateExternal).Select(r => r.Line!.LineNumber).ShouldBe([5, 9]);
        results.ShouldNotContain(r => r.Outcome == ReconciliationOutcome.MissingExternal);
    }

    [Fact]
    public void In_scope_records_no_line_refers_to_are_missing_external()
    {
        var reported = Payment(100.00m);
        var missing = Payment(50.00m);
        var refund = Refund(missing, 20.00m);

        var results = Match([Line(1, reported, "CAPTURED")], [reported, missing], [refund]);

        results.Where(r => r.Outcome == ReconciliationOutcome.MissingExternal).Select(r => r.InternalId).ShouldBe([missing.PaymentId, refund.RefundId], ignoreOrder: true);
    }

    [Fact]
    public void A_line_outside_the_period_still_matches_by_reference()
    {
        var p = Payment(100.00m);

        Match([Line(1, p, "CAPTURED")], [p], scope: []).Single().Outcome.ShouldBe(ReconciliationOutcome.Matched);
    }

    [Theory]
    [InlineData(StatementTransactionType.PAYMENT, "Succeeded", "CAPTURED", true)]
    [InlineData(StatementTransactionType.PAYMENT, "PartiallyRefunded", "CAPTURED", true)]
    [InlineData(StatementTransactionType.PAYMENT, "Refunded", "CAPTURED", true)]
    [InlineData(StatementTransactionType.PAYMENT, "Reversed", "REVERSED", true)]
    [InlineData(StatementTransactionType.PAYMENT, "Reversed", "CAPTURED", false)]
    [InlineData(StatementTransactionType.PAYMENT, "Succeeded", "REVERSED", false)]
    [InlineData(StatementTransactionType.PAYMENT, "Declined", "DECLINED", true)]
    [InlineData(StatementTransactionType.PAYMENT, "Failed", "FAILED", true)]
    [InlineData(StatementTransactionType.PAYMENT, "Declined", "CAPTURED", false)]
    [InlineData(StatementTransactionType.PAYMENT, "Pending", "CAPTURED", false)]
    [InlineData(StatementTransactionType.REFUND, "Succeeded", "REFUNDED", true)]
    [InlineData(StatementTransactionType.REFUND, "Failed", "FAILED", true)]
    [InlineData(StatementTransactionType.REFUND, "Succeeded", "FAILED", false)]
    [InlineData(StatementTransactionType.REFUND, "Pending", "REFUNDED", false)]
    public void Status_mapping(StatementTransactionType type, string internalStatus, string externalStatus, bool agree) =>
        ReconciliationEngine.StatusesAgree(type, internalStatus, externalStatus).ShouldBe(agree);

    [Fact]
    public void Fingerprints_are_stable_across_runs_and_distinct_per_discrepancy()
    {
        var statement = Guid.CreateVersion7();
        var p = Payment(100.00m);
        var lines = new[] { Line(1, p, "CAPTURED", amount: 99m), Line(2, p, "CAPTURED") };

        var first = Match(lines, [p]).Select(r => Convert.ToHexString(ReconciliationEngine.Fingerprint(statement, r))).ToList();
        var second = Match(lines, [p]).Select(r => Convert.ToHexString(ReconciliationEngine.Fingerprint(statement, r))).ToList();

        second.ShouldBe(first);
        first.Distinct().Count().ShouldBe(2);
        Convert.ToHexString(ReconciliationEngine.Fingerprint(Guid.CreateVersion7(), Match(lines, [p])[0])).ShouldNotBe(first[0]); // per statement
    }

    [Fact]
    public void Csv_round_trips_and_rejects_every_bad_line_at_once()
    {
        var p = Payment(19.99m);
        var csv = StatementCsv.Write([new ParsedLine(0, "EXT-1", PublicId.Create(IdPrefixes.Payment, p.PaymentId), StatementTransactionType.PAYMENT, 19.99m, "ZAR", "CAPTURED", new DateOnly(2026, 11, 17), "")]);

        var (lines, errors) = StatementCsv.Parse(csv);
        errors.ShouldBeEmpty();
        lines.Single().Amount.ShouldBe(19.99m);
        lines.Single().LineNumber.ShouldBe(2);

        var bad = $"""
            {StatementCsv.Header}
            EXT-1,pay_x,PAYMENT,10.001,ZAR,CAPTURED,2026-11-17
            EXT-2,pay_x,REFUND,10.00,ZAR,CAPTURED,2026-11-17
            EXT-3,pay_x,PAYMENT,-5,XXX,CAPTURED,17/11/2026
            EXT-4,pay_x,PAYMENT,10.00,ZAR
            EXT-5,"pay_x",PAYMENT,10.00,ZAR,CAPTURED,2026-11-17
            EXT-6,pay_x,PAYMENT,10.00,ZAR,CAPTURED,2026-11-17
            """;
        var (_, badErrors) = StatementCsv.Parse(bad);
        badErrors.Keys.ShouldBe(["line 2", "line 3", "line 4", "line 5", "line 6"], ignoreOrder: true);
        badErrors["line 4"].Length.ShouldBe(2); // currency and date

        StatementCsv.Parse("id,amount\n1,2\n").Errors.Keys.ShouldBe(["line 1"]);
        StatementCsv.Parse(StatementCsv.Header + "\n").Errors.Keys.ShouldBe(["file"]);
    }

    private static PaymentRecord Payment(decimal amount, string currency = "ZAR", PaymentStatus status = PaymentStatus.Succeeded) =>
        new(Guid.CreateVersion7(), Merchant, amount, currency, status, Completed);

    private static RefundRecord Refund(PaymentRecord payment, decimal amount) =>
        new(Guid.CreateVersion7(), payment.PaymentId, Merchant, amount, payment.Currency, RefundStatus.Succeeded, Completed);

    private static StatementLine Line(int number, PaymentRecord p, string status, decimal? amount = null, string? currency = null) =>
        Line(number, PublicId.Create(IdPrefixes.Payment, p.PaymentId), StatementTransactionType.PAYMENT, amount ?? p.Amount, currency ?? p.Currency, status);

    private static StatementLine Line(int number, string reference, StatementTransactionType type, decimal amount, string currency, string status) => new()
    {
        Id = number,
        LineNumber = number,
        ExternalId = $"EXT-{number}",
        Reference = reference,
        TransactionType = type,
        Amount = amount,
        Currency = currency,
        ExternalStatus = status,
        ValueDate = DateOnly.FromDateTime(Completed.UtcDateTime),
        Raw = string.Empty,
    };

    private static IReadOnlyList<EngineResult> Match(
        IReadOnlyList<StatementLine> lines, PaymentRecord[] payments, RefundRecord[]? refunds = null, PaymentRecord[]? scope = null) =>
        ReconciliationEngine.Match(
            lines,
            payments.ToDictionary(p => p.PaymentId),
            (refunds ?? []).ToDictionary(r => r.RefundId),
            scope ?? payments,
            refunds ?? []);
}
