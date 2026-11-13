using PayFlow.Modules.Ledger.Contracts;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Ledger.Domain;

internal enum AccountType
{
    Asset,
    Liability,
    Revenue,
    Expense,
}

internal sealed class LedgerAccount
{
    public Guid Id { get; init; }

    public required string Code { get; init; }

    public LedgerAccountKind Kind { get; init; }

    public AccountType Type { get; init; }

    public Guid? MerchantId { get; init; }

    public required string Currency { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public static AccountType TypeOf(LedgerAccountKind kind) => kind switch
    {
        LedgerAccountKind.MerchantPayable => AccountType.Liability,
        LedgerAccountKind.FeeRevenue => AccountType.Revenue,
        _ => AccountType.Asset,
    };

    /// <summary>Assets and expenses grow with debits; liabilities and revenue with credits.</summary>
    public static bool IsDebitNormal(AccountType type) => type is AccountType.Asset or AccountType.Expense;

    public static string CodeFor(LedgerAccountKind kind, Guid? merchantId, string currency) =>
        kind == LedgerAccountKind.MerchantPayable
            ? $"merchant_payable:{merchantId:N}:{currency}"
            : $"{ToSnake(kind)}:{currency}";

    private static string ToSnake(LedgerAccountKind kind) => kind switch
    {
        LedgerAccountKind.AcquirerReceivable => "acquirer_receivable",
        LedgerAccountKind.FeeRevenue => "fee_revenue",
        LedgerAccountKind.PayoutCash => "payout_cash",
        LedgerAccountKind.ReconciliationSuspense => "reconciliation_suspense",
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };
}

internal sealed class JournalEntry
{
    public Guid Id { get; init; }

    public required string EntryType { get; init; }

    public required string SourceType { get; init; }

    public Guid SourceId { get; init; }

    public required string Currency { get; init; }

    public required string Description { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public required string CorrelationId { get; init; }

    public List<Posting> Postings { get; init; } = [];
}

internal sealed class Posting
{
    public long Id { get; init; }

    public Guid JournalEntryId { get; init; }

    public Guid AccountId { get; init; }

    public PostingDirection Direction { get; init; }

    public decimal Amount { get; init; }
}

/// <summary>Pure validation of a requested entry, before any database work.</summary>
internal static class JournalRules
{
    public static Result Validate(JournalEntryRequest entry)
    {
        if (!Currency.TryFromCode(entry.Currency, out var currency))
        {
            return Error.Validation("ledger_unsupported_currency", $"Unsupported currency {entry.Currency}.");
        }

        if (entry.Postings.Count < 2)
        {
            return Error.Validation("ledger_too_few_postings", "A journal entry needs at least two postings.");
        }

        foreach (var p in entry.Postings)
        {
            if (p.Amount <= 0m || !Money.HasValidPrecision(p.Amount, currency))
            {
                return Error.Validation("ledger_invalid_amount", $"Posting amounts must be positive {currency.Code} amounts.");
            }

            if ((p.Account == LedgerAccountKind.MerchantPayable) != p.MerchantId.HasValue)
            {
                return Error.Validation("ledger_invalid_account", "Only merchant payable accounts take a merchant id.");
            }
        }

        var debits = entry.Postings.Where(p => p.Direction == PostingDirection.Debit).Sum(p => p.Amount);
        var credits = entry.Postings.Where(p => p.Direction == PostingDirection.Credit).Sum(p => p.Amount);
        return debits == credits
            ? Result.Success()
            : Error.Validation("ledger_unbalanced", $"Debits {debits} do not equal credits {credits}.");
    }
}
