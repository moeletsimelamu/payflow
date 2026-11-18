using System.Globalization;
using System.Text;
using PayFlow.Modules.Reconciliation.Domain;
using PayFlow.SharedKernel.Money;

namespace PayFlow.Modules.Reconciliation.Application;

/// <summary>A parsed line, before it is attached to a statement.</summary>
internal sealed record ParsedLine(
    int LineNumber,
    string ExternalId,
    string Reference,
    StatementTransactionType Type,
    decimal Amount,
    string Currency,
    string Status,
    DateOnly ValueDate,
    string Raw);

/// <summary>
/// The simulated acquirer's statement format (docs/RECONCILIATION.md §2). Strict on purpose: a file
/// with any malformed line is rejected whole, with every error listed, so nothing is half-imported.
/// Fields are plain values; quoting is not supported.
/// </summary>
internal static class StatementCsv
{
    public const string Header = "external_id,payflow_reference,type,amount,currency,status,value_date";
    public const int MaxLines = 50_000;
    public const int MaxErrorsReported = 50;

    private static readonly Dictionary<StatementTransactionType, string[]> StatusesByType = new()
    {
        [StatementTransactionType.PAYMENT] = ["CAPTURED", "REVERSED", "DECLINED", "FAILED"],
        [StatementTransactionType.REFUND] = ["REFUNDED", "FAILED"],
    };

    public static (IReadOnlyList<ParsedLine> Lines, IReadOnlyDictionary<string, string[]> Errors) Parse(string csv)
    {
        var lines = new List<ParsedLine>();
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var physical = csv.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');

        if (physical.Length == 0 || !string.Equals(physical[0].TrimEnd(), Header, StringComparison.Ordinal))
        {
            errors["line 1"] = [$"The header must be exactly '{Header}'."];
            return (lines, errors);
        }

        for (var i = 1; i < physical.Length; i++)
        {
            var raw = physical[i];
            if (raw.Length == 0 && i == physical.Length - 1)
            {
                break; // trailing newline
            }

            var lineNumber = i + 1;
            if (lines.Count >= MaxLines)
            {
                errors["file"] = [$"A statement may contain at most {MaxLines} lines."];
                break;
            }

            var problems = new List<string>();
            var parsed = ParseLine(lineNumber, raw, problems);
            if (problems.Count > 0)
            {
                if (errors.Count < MaxErrorsReported)
                {
                    errors[$"line {lineNumber.ToString(CultureInfo.InvariantCulture)}"] = [.. problems];
                }
            }
            else
            {
                lines.Add(parsed!);
            }
        }

        if (errors.Count == 0 && lines.Count == 0)
        {
            errors["file"] = ["The statement has no lines."];
        }

        return (lines, errors);
    }

    public static string Write(IEnumerable<ParsedLine> lines)
    {
        var csv = new StringBuilder(Header).Append('\n');
        foreach (var l in lines)
        {
            csv.Append(Format(l)).Append('\n');
        }

        return csv.ToString();
    }

    public static string Format(ParsedLine l) => string.Join(
        ',',
        l.ExternalId,
        l.Reference,
        l.Type.ToString(),
        Money.Of(l.Amount, l.Currency).ToAmountString(),
        l.Currency,
        l.Status,
        l.ValueDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));

    private static ParsedLine? ParseLine(int lineNumber, string raw, List<string> problems)
    {
        if (raw.Contains('"', StringComparison.Ordinal))
        {
            problems.Add("Quoted fields are not supported.");
            return null;
        }

        var f = raw.TrimEnd().Split(',');
        if (f.Length != 7)
        {
            problems.Add($"Expected 7 fields, found {f.Length}.");
            return null;
        }

        var externalId = f[0];
        var reference = f[1];
        if (externalId.Length is 0 or > 100)
        {
            problems.Add("external_id is required (at most 100 characters).");
        }

        if (reference.Length is 0 or > 100)
        {
            problems.Add("payflow_reference is required (at most 100 characters).");
        }

        var typeOk = Enum.TryParse<StatementTransactionType>(f[2], ignoreCase: false, out var type) && Enum.IsDefined(type) && f[2] == type.ToString();
        if (!typeOk)
        {
            problems.Add("type must be PAYMENT or REFUND.");
        }

        var amount = 0m;
        if (!SharedKernel.Money.Currency.TryFromCode(f[4], out var currency))
        {
            problems.Add($"currency '{f[4]}' is not supported.");
        }
        else if (!Money.TryParse(f[3], currency, out var money) || !money.IsPositive)
        {
            problems.Add($"amount '{f[3]}' must be a positive amount with at most {currency.MinorUnits} decimal places.");
        }
        else
        {
            amount = money.Amount;
        }

        if (typeOk && !StatusesByType[type].Contains(f[5], StringComparer.Ordinal))
        {
            problems.Add($"status for {type} must be one of {string.Join(", ", StatusesByType[type])}.");
        }

        if (!DateOnly.TryParseExact(f[6], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var valueDate))
        {
            problems.Add("value_date must be yyyy-MM-dd.");
        }

        return problems.Count > 0
            ? null
            : new ParsedLine(lineNumber, externalId, reference, type, amount, f[4], f[5], valueDate, raw.TrimEnd());
    }
}
