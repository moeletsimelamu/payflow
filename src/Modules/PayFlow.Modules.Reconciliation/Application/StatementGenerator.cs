using System.Globalization;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Reconciliation.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Reconciliation.Application;

/// <summary>How many of each discrepancy to plant in a generated statement.</summary>
public sealed record InjectCounts(
    int MissingExternal = 0,
    int MissingInternal = 0,
    int AmountMismatch = 0,
    int CurrencyMismatch = 0,
    int DuplicateExternal = 0,
    int StatusDiscrepancy = 0)
{
    public int RecordsNeeded => MissingExternal + AmountMismatch + CurrencyMismatch + DuplicateExternal + StatusDiscrepancy;
}

public sealed record GenerateStatementRequest(DateOnly PeriodStart, DateOnly PeriodEnd, string? SourceName, InjectCounts? Inject);

public sealed record InjectedDiscrepancy(ReconciliationOutcome Outcome, string Reference, string Detail);

public sealed record GeneratedStatement(
    string SourceName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string FileName,
    int LineCount,
    string Csv,
    IReadOnlyList<InjectedDiscrepancy> Injected);

/// <summary>
/// Plays the simulated acquirer: writes the statement it would send for a period, from PayFlow's
/// own records, with chosen discrepancies planted so every matching outcome can be demonstrated.
/// Records are taken in completion order, so the same data and counts give the same assignments.
/// </summary>
internal sealed class StatementGenerator(IPaymentLookup payments, IRefundLookup refunds, IdGenerator ids)
{
    public const string DefaultSourceName = "Simulated Acquirer";
    public const int MaxPerKind = 100;

    public async Task<Result<GeneratedStatement>> GenerateAsync(GenerateStatementRequest request, CancellationToken ct)
    {
        var inject = request.Inject ?? new InjectCounts();
        var from = new DateTimeOffset(request.PeriodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(request.PeriodEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

        var records = (await payments.ListAcquirerVisibleAsync(from, to, ct))
            .Select(p => new Source(ReconciliationEngine.PaymentType, PublicId.Create(IdPrefixes.Payment, p.PaymentId), StatementTransactionType.PAYMENT,
                p.Amount, p.Currency, p.Status == PaymentStatus.Reversed ? "REVERSED" : "CAPTURED", p.CompletedAt!.Value))
            .Concat((await refunds.ListAcquirerVisibleAsync(from, to, ct))
                .Select(r => new Source(ReconciliationEngine.RefundType, PublicId.Create(IdPrefixes.Refund, r.RefundId), StatementTransactionType.REFUND,
                    r.Amount, r.Currency, "REFUNDED", r.CompletedAt!.Value)))
            .OrderBy(s => s.CompletedAt).ThenBy(s => s.Reference, StringComparer.Ordinal)
            .ToList();

        if (inject.RecordsNeeded > records.Count)
        {
            return Error.Unprocessable(
                "not_enough_transactions",
                $"The period has {records.Count} transactions; the requested discrepancies need {inject.RecordsNeeded}.");
        }

        var lines = new List<ParsedLine>();
        var injected = new List<InjectedDiscrepancy>();
        var next = 0;

        void Plant(int count, ReconciliationOutcome outcome, Func<Source, (ParsedLine? Line, string Detail)> mutate)
        {
            for (var i = 0; i < count; i++)
            {
                var source = records[next++];
                var (line, detail) = mutate(source);
                if (line is not null)
                {
                    lines.Add(line);
                }

                injected.Add(new InjectedDiscrepancy(outcome, source.Reference, detail));
            }
        }

        Plant(inject.MissingExternal, ReconciliationOutcome.MissingExternal, _ => (null, "Left off the statement."));
        Plant(inject.AmountMismatch, ReconciliationOutcome.AmountMismatch, s =>
        {
            var reported = s.Amount + 1m;
            return (Line(s) with { Amount = reported }, string.Create(CultureInfo.InvariantCulture, $"Reported {reported} instead of {s.Amount}."));
        });
        Plant(inject.CurrencyMismatch, ReconciliationOutcome.CurrencyMismatch, s =>
        {
            var other = Currency.All.Where(c => c.Code != s.Currency && Money.HasValidPrecision(s.Amount, c)).OrderBy(c => c.Code, StringComparer.Ordinal).FirstOrDefault();
            if (other is null)
            {
                // Only a 3-decimal amount fits no other currency: report it in USD, rounded away from zero.
                var usd = decimal.Round(s.Amount, Currency.USD.MinorUnits, Money.RoundingMode);
                return (Line(s) with { Currency = Currency.USD.Code, Amount = usd }, $"Reported in USD instead of {s.Currency}.");
            }

            return (Line(s) with { Currency = other.Code }, $"Reported in {other.Code} instead of {s.Currency}.");
        });
        Plant(inject.StatusDiscrepancy, ReconciliationOutcome.StatusDiscrepancy, s =>
        {
            var status = s.Status switch { "CAPTURED" => "REVERSED", "REVERSED" => "CAPTURED", _ => "FAILED" };
            return (Line(s) with { Status = status }, $"Reported {status} instead of {s.Status}.");
        });
        Plant(inject.DuplicateExternal, ReconciliationOutcome.DuplicateExternal, s =>
        {
            lines.Add(Line(s));
            return (Line(s) with { ExternalId = $"{ExternalId(s)}-DUP" }, "Reported twice.");
        });

        // Everything else is reported faithfully.
        lines.AddRange(records.Skip(next).Select(Line));

        for (var i = 0; i < inject.MissingInternal; i++)
        {
            var reference = PublicId.Create(IdPrefixes.Payment, ids.NewId());
            lines.Add(new ParsedLine(0, $"SIM-UNKNOWN-{i + 1:D4}", reference, StatementTransactionType.PAYMENT, 100m, Currency.ZAR.Code, "CAPTURED", request.PeriodStart, string.Empty));
            injected.Add(new InjectedDiscrepancy(ReconciliationOutcome.MissingInternal, reference, "A transaction PayFlow never processed."));
        }

        var ordered = lines.OrderBy(l => l.ValueDate).ThenBy(l => l.ExternalId, StringComparer.Ordinal).ToList();
        var period = $"{request.PeriodStart:yyyyMMdd}-{request.PeriodEnd:yyyyMMdd}";
        return new GeneratedStatement(
            request.SourceName ?? DefaultSourceName,
            request.PeriodStart,
            request.PeriodEnd,
            $"statement-{period}.csv",
            ordered.Count,
            StatementCsv.Write(ordered),
            injected);
    }

    private static string ExternalId(Source s) => $"SIM-{s.Reference.ToUpperInvariant()}";

    private static ParsedLine Line(Source s) => new(
        0, ExternalId(s), s.Reference, s.Type, s.Amount, s.Currency, s.Status, DateOnly.FromDateTime(s.CompletedAt.UtcDateTime), string.Empty);

    private sealed record Source(string Kind, string Reference, StatementTransactionType Type, decimal Amount, string Currency, string Status, DateTimeOffset CompletedAt);
}
