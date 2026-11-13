namespace PayFlow.Modules.Ledger.Contracts;

/// <summary>Ledger accounts (docs/DATABASE.md, ledger schema).</summary>
public enum LedgerAccountKind
{
    /// <summary>Asset: funds the simulated acquirer owes PayFlow.</summary>
    AcquirerReceivable,

    /// <summary>Liability, per merchant: what PayFlow owes the merchant.</summary>
    MerchantPayable,

    /// <summary>Revenue: PayFlow processing fees.</summary>
    FeeRevenue,

    /// <summary>Asset: simulated PayFlow bank account used for payouts.</summary>
    PayoutCash,

    /// <summary>Asset: holds approved reconciliation adjustments.</summary>
    ReconciliationSuspense,
}

public enum PostingDirection
{
    Debit,
    Credit,
}

/// <summary>Journal entry types. One entry per (source, type) can ever exist.</summary>
public static class JournalEntryTypes
{
    public const string PaymentCaptured = "PaymentCaptured";
    public const string PaymentFee = "PaymentFee";
    public const string RefundSucceeded = "RefundSucceeded";
    public const string PaymentReversed = "PaymentReversed";
    public const string ReversalFeeReturned = "ReversalFeeReturned";
    public const string SettlementPaid = "SettlementPaid";
    public const string ReconciliationAdjustment = "ReconciliationAdjustment";
}

/// <summary>Merchant accounts need <see cref="MerchantId"/>; platform accounts must leave it null.</summary>
public sealed record PostingRequest(LedgerAccountKind Account, Guid? MerchantId, PostingDirection Direction, decimal Amount);

public sealed record JournalEntryRequest(
    string EntryType,
    string SourceType,
    Guid SourceId,
    string Currency,
    string Description,
    IReadOnlyList<PostingRequest> Postings);

public sealed record PostingView(string Account, PostingDirection Direction, decimal Amount);

public sealed record JournalEntryView(Guid Id, string EntryType, string Currency, string Description, DateTimeOffset OccurredAt, IReadOnlyList<PostingView> Postings);

/// <summary>
/// The double-entry ledger, the financial source of truth. Entries are added to the caller's unit of
/// work and commit atomically with the business change that caused them.
/// </summary>
public interface ILedger
{
    /// <summary>
    /// Records a balanced entry. Throws if debits ≠ credits, an amount is not positive or exceeds the
    /// currency's precision, or an account/merchant combination is invalid. The database verifies
    /// the balance again at commit.
    /// </summary>
    Task PostAsync(JournalEntryRequest entry, CancellationToken cancellationToken);

    Task<IReadOnlyList<JournalEntryView>> GetEntriesForSourceAsync(string sourceType, Guid sourceId, CancellationToken cancellationToken);

    /// <summary>Signed balance in the account's normal direction (positive = normal balance).</summary>
    Task<decimal> GetBalanceAsync(LedgerAccountKind account, Guid? merchantId, string currency, CancellationToken cancellationToken);
}
