using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Ledger.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Ledger.Application;

internal sealed class EfLedger(PayFlowDbContext db, IdGenerator ids, TimeProvider clock, ICorrelationContext correlation) : ILedger
{
    private readonly Dictionary<string, Guid> _accounts = new(StringComparer.Ordinal);

    public async Task PostAsync(JournalEntryRequest entry, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var valid = JournalRules.Validate(entry);
        if (!valid.IsSuccess)
        {
            // A caller asking for an invalid entry is a programming error, never a user error.
            throw new DomainException(valid.Error);
        }

        var journal = new JournalEntry
        {
            Id = ids.NewId(),
            EntryType = entry.EntryType,
            SourceType = entry.SourceType,
            SourceId = entry.SourceId,
            Currency = entry.Currency,
            Description = entry.Description,
            OccurredAt = clock.GetUtcNow(),
            CorrelationId = correlation.CorrelationId,
        };

        foreach (var p in entry.Postings)
        {
            journal.Postings.Add(new Posting
            {
                JournalEntryId = journal.Id,
                AccountId = await ResolveAccountAsync(p.Account, p.MerchantId, entry.Currency, cancellationToken),
                Direction = p.Direction,
                Amount = p.Amount,
            });
        }

        db.Set<JournalEntry>().Add(journal);
    }

    public async Task<IReadOnlyList<JournalEntryView>> GetEntriesForSourceAsync(string sourceType, Guid sourceId, CancellationToken cancellationToken)
    {
        var entries = await db.Set<JournalEntry>().AsNoTracking()
            .Where(e => e.SourceType == sourceType && e.SourceId == sourceId)
            .Include(e => e.Postings)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(cancellationToken);
        var accountIds = entries.SelectMany(e => e.Postings).Select(p => p.AccountId).Distinct().ToList();
        var codes = await db.Set<LedgerAccount>().AsNoTracking()
            .Where(a => accountIds.Contains(a.Id))
            .ToDictionaryAsync(a => a.Id, a => a.Kind.ToString(), cancellationToken);
        return entries.Select(e => new JournalEntryView(
                e.Id,
                e.EntryType,
                e.Currency,
                e.Description,
                e.OccurredAt,
                e.Postings.OrderBy(p => p.Direction).Select(p => new PostingView(codes[p.AccountId], p.Direction, p.Amount)).ToList()))
            .ToList();
    }

    public async Task<decimal> GetBalanceAsync(LedgerAccountKind account, Guid? merchantId, string currency, CancellationToken cancellationToken)
    {
        var code = LedgerAccount.CodeFor(account, merchantId, currency);
        var accountId = await db.Set<LedgerAccount>().Where(a => a.Code == code).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(cancellationToken);
        if (accountId is null)
        {
            return 0m;
        }

        var debits = await db.Set<Posting>().Where(p => p.AccountId == accountId && p.Direction == PostingDirection.Debit).SumAsync(p => p.Amount, cancellationToken);
        var credits = await db.Set<Posting>().Where(p => p.AccountId == accountId && p.Direction == PostingDirection.Credit).SumAsync(p => p.Amount, cancellationToken);
        return LedgerAccount.IsDebitNormal(LedgerAccount.TypeOf(account)) ? debits - credits : credits - debits;
    }

    /// <summary>Gets or creates the account. Creation is race-free: the unique code makes concurrent inserts idempotent.</summary>
    private async Task<Guid> ResolveAccountAsync(LedgerAccountKind kind, Guid? merchantId, string currency, CancellationToken ct)
    {
        var code = LedgerAccount.CodeFor(kind, merchantId, currency);
        if (_accounts.TryGetValue(code, out var cached))
        {
            return cached;
        }

        var id = ids.NewId();
        var kindName = kind.ToString();
        var typeName = LedgerAccount.TypeOf(kind).ToString();
        var now = clock.GetUtcNow();
        await db.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO ledger.accounts (id, code, kind, type, merchant_id, currency, created_at)
            VALUES ({id}, {code}, {kindName}, {typeName}, {merchantId}, {currency}, {now})
            ON CONFLICT (code) DO NOTHING
            """,
            ct);
        var accountId = await db.Set<LedgerAccount>().Where(a => a.Code == code).Select(a => a.Id).SingleAsync(ct);
        _accounts[code] = accountId;
        return accountId;
    }
}
