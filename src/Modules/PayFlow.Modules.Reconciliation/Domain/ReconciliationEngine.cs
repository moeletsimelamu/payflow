using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;

namespace PayFlow.Modules.Reconciliation.Domain;

/// <summary>The engine's verdict on one line or one internal record.</summary>
internal sealed record EngineResult(
    ReconciliationOutcome Outcome,
    StatementLine? Line,
    string? InternalType,
    Guid? InternalId,
    decimal? InternalAmount,
    string? InternalCurrency,
    string? InternalStatus,
    string Detail);

/// <summary>
/// Deterministic matching (docs/PAYMENT_LIFECYCLE.md §10, REC-3). Pure: no I/O, so the rules are
/// unit-tested directly.
/// <list type="number">
/// <item>Lines are grouped by (type, reference) in file order; the first of each group is evaluated,
/// the rest are <see cref="ReconciliationOutcome.DuplicateExternal"/>.</item>
/// <item>Evaluated lines, by precedence: MissingInternal → CurrencyMismatch → AmountMismatch
/// (exact, zero tolerance) → StatusDiscrepancy → Matched.</item>
/// <item>In-scope internal records that no line refers to are MissingExternal.</item>
/// </list>
/// </summary>
internal static class ReconciliationEngine
{
    public const string PaymentType = "payment";
    public const string RefundType = "refund";

    public static readonly IReadOnlySet<string> AllowedExternalStatuses =
        new HashSet<string>(["CAPTURED", "REVERSED", "DECLINED", "FAILED", "REFUNDED"], StringComparer.Ordinal);

    public static IReadOnlyList<EngineResult> Match(
        IReadOnlyList<StatementLine> lines,
        IReadOnlyDictionary<Guid, PaymentRecord> payments,
        IReadOnlyDictionary<Guid, RefundRecord> refunds,
        IReadOnlyList<PaymentRecord> paymentsInScope,
        IReadOnlyList<RefundRecord> refundsInScope)
    {
        var results = new List<EngineResult>();
        var referenced = new HashSet<Guid>();

        foreach (var group in lines.OrderBy(l => l.LineNumber).GroupBy(l => (l.TransactionType, l.Reference)))
        {
            var first = true;
            foreach (var line in group)
            {
                var resolved = Resolve(line, payments, refunds);
                if (resolved.Id is { } id)
                {
                    referenced.Add(id);
                }

                if (!first)
                {
                    results.Add(new EngineResult(ReconciliationOutcome.DuplicateExternal, line, resolved.Type, resolved.Id, resolved.Amount, resolved.Currency, resolved.Status,
                        $"Line {line.LineNumber} repeats {line.TransactionType} {line.Reference} already reported on line {group.First().LineNumber}."));
                    continue;
                }

                first = false;
                results.Add(Evaluate(line, resolved));
            }
        }

        foreach (var p in paymentsInScope.Where(p => !referenced.Contains(p.PaymentId)))
        {
            results.Add(new EngineResult(ReconciliationOutcome.MissingExternal, null, PaymentType, p.PaymentId, p.Amount, p.Currency, p.Status.ToString(),
                $"Payment {PublicId.Create(IdPrefixes.Payment, p.PaymentId)} ({p.Status}) is not on the statement."));
        }

        foreach (var r in refundsInScope.Where(r => !referenced.Contains(r.RefundId)))
        {
            results.Add(new EngineResult(ReconciliationOutcome.MissingExternal, null, RefundType, r.RefundId, r.Amount, r.Currency, r.Status.ToString(),
                $"Refund {PublicId.Create(IdPrefixes.Refund, r.RefundId)} is not on the statement."));
        }

        return results;
    }

    /// <summary>Does the acquirer's status agree with PayFlow's?</summary>
    public static bool StatusesAgree(StatementTransactionType type, string internalStatus, string externalStatus) => type switch
    {
        StatementTransactionType.PAYMENT => internalStatus switch
        {
            nameof(PaymentStatus.Succeeded) or nameof(PaymentStatus.PartiallyRefunded) or nameof(PaymentStatus.Refunded) => externalStatus == "CAPTURED",
            nameof(PaymentStatus.Reversed) => externalStatus == "REVERSED",
            nameof(PaymentStatus.Declined) or nameof(PaymentStatus.Failed) or nameof(PaymentStatus.Expired) => externalStatus is "DECLINED" or "FAILED",
            _ => false,
        },
        StatementTransactionType.REFUND => internalStatus switch
        {
            nameof(RefundStatus.Succeeded) => externalStatus == "REFUNDED",
            nameof(RefundStatus.Failed) => externalStatus == "FAILED",
            _ => false,
        },
        _ => false,
    };

    /// <summary>Stable identity of a discrepancy within a statement, so reruns never duplicate exceptions.</summary>
    public static byte[] Fingerprint(Guid statementId, EngineResult r)
    {
        var line = r.Outcome is ReconciliationOutcome.DuplicateExternal or ReconciliationOutcome.MissingInternal
            ? r.Line?.LineNumber.ToString(CultureInfo.InvariantCulture)
            : string.Empty;
        var key = $"{statementId:N}|{r.Outcome}|{r.InternalType}|{r.InternalId:N}|{r.Line?.TransactionType}|{r.Line?.Reference}|{line}";
        return SHA256.HashData(Encoding.UTF8.GetBytes(key));
    }

    /// <summary>Amounts in the currency's own precision ("250.00", not the stored "250.0000").</summary>
    internal static string Format(decimal amount, string currency) =>
        Currency.TryFromCode(currency, out var c) && Money.HasValidPrecision(amount, c)
            ? Money.Of(amount, c).ToAmountString()
            : amount.ToString(CultureInfo.InvariantCulture);

    private static EngineResult Evaluate(StatementLine line, (string? Type, Guid? Id, decimal? Amount, string? Currency, string? Status) found)
    {
        if (found.Id is null)
        {
            return new EngineResult(ReconciliationOutcome.MissingInternal, line, null, null, null, null, null,
                $"No PayFlow {line.TransactionType.ToString().ToLowerInvariant()} matches reference '{line.Reference}'.");
        }

        if (!string.Equals(found.Currency, line.Currency, StringComparison.Ordinal))
        {
            return new EngineResult(ReconciliationOutcome.CurrencyMismatch, line, found.Type, found.Id, found.Amount, found.Currency, found.Status,
                $"PayFlow recorded {found.Currency}; the statement says {line.Currency}.");
        }

        if (found.Amount != line.Amount)
        {
            return new EngineResult(ReconciliationOutcome.AmountMismatch, line, found.Type, found.Id, found.Amount, found.Currency, found.Status,
                $"PayFlow recorded {Format(found.Amount!.Value, found.Currency!)} {found.Currency}; the statement says {Format(line.Amount, line.Currency)} {line.Currency} (difference {Format(line.Amount - found.Amount.Value, line.Currency)}).");
        }

        if (!StatusesAgree(line.TransactionType, found.Status!, line.ExternalStatus))
        {
            return new EngineResult(ReconciliationOutcome.StatusDiscrepancy, line, found.Type, found.Id, found.Amount, found.Currency, found.Status,
                $"PayFlow {found.Type} status is {found.Status}; the statement says {line.ExternalStatus}.");
        }

        return new EngineResult(ReconciliationOutcome.Matched, line, found.Type, found.Id, found.Amount, found.Currency, found.Status, "Matched.");
    }

    private static (string? Type, Guid? Id, decimal? Amount, string? Currency, string? Status) Resolve(
        StatementLine line, IReadOnlyDictionary<Guid, PaymentRecord> payments, IReadOnlyDictionary<Guid, RefundRecord> refunds)
    {
        if (line.TransactionType == StatementTransactionType.PAYMENT &&
            PublicId.TryParse(line.Reference, IdPrefixes.Payment, out var pid) && payments.TryGetValue(pid.Value, out var p))
        {
            return (PaymentType, p.PaymentId, p.Amount, p.Currency, p.Status.ToString());
        }

        if (line.TransactionType == StatementTransactionType.REFUND &&
            PublicId.TryParse(line.Reference, IdPrefixes.Refund, out var rid) && refunds.TryGetValue(rid.Value, out var r))
        {
            return (RefundType, r.RefundId, r.Amount, r.Currency, r.Status.ToString());
        }

        return (null, null, null, null, null);
    }
}
