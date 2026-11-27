using System.Collections.Concurrent;
using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Ledger.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Ledger.Application;

/// <summary>
/// Committed account ids by code, shared by every request. Accounts are append-only (a trigger rejects
/// UPDATE and DELETE) and a code is unique, so a committed mapping can never become wrong. Without it
/// every payment spent six round trips, and a write lock on ledger.accounts, re-resolving the same
/// three accounts. Only ids read back by a plain SELECT (which sees committed rows only) go in here:
/// an account created inside the caller's still-open transaction could be rolled back, and other
/// transactions cannot reference it until it commits.
/// </summary>
internal sealed class LedgerAccountCache
{
    private readonly ConcurrentDictionary<string, Guid> _ids = new(StringComparer.Ordinal);

    public bool TryGet(string code, out Guid id) => _ids.TryGetValue(code, out id);

    public void AddCommitted(string code, Guid id) => _ids.TryAdd(code, id);
}

internal sealed class EfLedger(PayFlowDbContext db, IdGenerator ids, TimeProvider clock, ICorrelationContext correlation, LedgerAccountCache accounts) : ILedger
{
    /// <summary>Accounts this unit of work created or found after inserting; not yet known to be committed.</summary>
    private readonly Dictionary<string, Guid> _uncommitted = new(StringComparer.Ordinal);

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
        if (accounts.TryGet(code, out var cached) || _uncommitted.TryGetValue(code, out cached))
        {
            return cached;
        }

        // This unit of work has not inserted the code (checked above), so a row this SELECT sees was
        // committed by someone else: safe to share.
        var existing = await db.Set<LedgerAccount>().AsNoTracking().Where(a => a.Code == code).Select(a => (Guid?)a.Id).SingleOrDefaultAsync(ct);
        if (existing is { } known)
        {
            accounts.AddCommitted(code, known);
            return known;
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
        // Either this transaction's own insert or a concurrent one it waited for; the next unit of work
        // that reads it with a plain SELECT promotes it to the shared cache.
        var accountId = await db.Set<LedgerAccount>().Where(a => a.Code == code).Select(a => a.Id).SingleAsync(ct);
        _uncommitted[code] = accountId;
        return accountId;
    }
}
