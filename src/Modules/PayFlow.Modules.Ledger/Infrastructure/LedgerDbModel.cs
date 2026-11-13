using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger.Domain;

namespace PayFlow.Modules.Ledger.Infrastructure;

internal sealed class LedgerDbModel : IModuleDbModel
{
    public const string Schema = "ledger";

    /// <summary>
    /// Deferred constraint triggers: at commit, every journal entry must have at least two postings,
    /// equal debits and credits, and only accounts in its own currency.
    /// </summary>
    public const string BalanceTriggersSql = """
        CREATE OR REPLACE FUNCTION ledger.check_journal_entry(entry_id uuid) RETURNS void
        LANGUAGE plpgsql AS $$
        DECLARE
            debits numeric; credits numeric; posting_count int; foreign_currency int;
        BEGIN
            SELECT coalesce(sum(p.amount) FILTER (WHERE p.direction = 'Debit'), 0),
                   coalesce(sum(p.amount) FILTER (WHERE p.direction = 'Credit'), 0),
                   count(*),
                   count(*) FILTER (WHERE a.currency <> e.currency)
              INTO debits, credits, posting_count, foreign_currency
              FROM ledger.journal_entries e
              LEFT JOIN ledger.postings p ON p.journal_entry_id = e.id
              LEFT JOIN ledger.accounts a ON a.id = p.account_id
             WHERE e.id = entry_id;
            IF posting_count < 2 THEN
                RAISE EXCEPTION 'ledger_violation: journal entry % has % postings', entry_id, posting_count;
            END IF;
            IF debits <> credits THEN
                RAISE EXCEPTION 'ledger_violation: journal entry % is unbalanced (debits %, credits %)', entry_id, debits, credits;
            END IF;
            IF foreign_currency > 0 THEN
                RAISE EXCEPTION 'ledger_violation: journal entry % posts to accounts in another currency', entry_id;
            END IF;
        END;
        $$;

        CREATE OR REPLACE FUNCTION ledger.trg_check_entry_from_posting() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM ledger.check_journal_entry(NEW.journal_entry_id);
            RETURN NULL;
        END;
        $$;

        CREATE OR REPLACE FUNCTION ledger.trg_check_entry_from_entry() RETURNS trigger
        LANGUAGE plpgsql AS $$
        BEGIN
            PERFORM ledger.check_journal_entry(NEW.id);
            RETURN NULL;
        END;
        $$;

        CREATE CONSTRAINT TRIGGER trg_postings_balanced
            AFTER INSERT ON ledger.postings
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION ledger.trg_check_entry_from_posting();

        CREATE CONSTRAINT TRIGGER trg_journal_entries_balanced
            AFTER INSERT ON ledger.journal_entries
            DEFERRABLE INITIALLY DEFERRED
            FOR EACH ROW EXECUTE FUNCTION ledger.trg_check_entry_from_entry();
        """;

    public const string DropBalanceTriggersSql = """
        DROP TRIGGER IF EXISTS trg_postings_balanced ON ledger.postings;
        DROP TRIGGER IF EXISTS trg_journal_entries_balanced ON ledger.journal_entries;
        DROP FUNCTION IF EXISTS ledger.trg_check_entry_from_posting();
        DROP FUNCTION IF EXISTS ledger.trg_check_entry_from_entry();
        DROP FUNCTION IF EXISTS ledger.check_journal_entry(uuid);
        """;

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<LedgerAccount>(e =>
        {
            e.ToTable("accounts", Schema, t => t.HasCheckConstraint(
                "ck_accounts_merchant", "(kind = 'MerchantPayable') = (merchant_id IS NOT NULL)"));
            e.HasKey(a => a.Id);
            e.Property(a => a.Code).HasMaxLength(120);
            e.Property(a => a.Kind).HasConversion<string>().HasMaxLength(40);
            e.Property(a => a.Type).HasConversion<string>().HasMaxLength(20);
            e.Property(a => a.Currency).HasColumnType("char(3)");
            e.HasIndex(a => a.Code).IsUnique();
            e.HasIndex(a => a.MerchantId);
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(a => a.Currency).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<JournalEntry>(e =>
        {
            e.ToTable("journal_entries", Schema);
            e.HasKey(j => j.Id);
            e.Property(j => j.EntryType).HasMaxLength(40);
            e.Property(j => j.SourceType).HasMaxLength(40);
            e.Property(j => j.Currency).HasColumnType("char(3)");
            e.Property(j => j.Description).HasMaxLength(300);
            e.Property(j => j.CorrelationId).HasMaxLength(64);
            e.HasIndex(j => new { j.SourceType, j.SourceId, j.EntryType }).IsUnique();
            e.HasIndex(j => j.OccurredAt);
            e.HasMany(j => j.Postings).WithOne().HasForeignKey(p => p.JournalEntryId);
        });

        modelBuilder.Entity<Posting>(e =>
        {
            e.ToTable("postings", Schema, t => t.HasCheckConstraint("ck_postings_amount", "amount > 0"));
            e.HasKey(p => p.Id);
            e.Property(p => p.Id).UseIdentityAlwaysColumn();
            e.Property(p => p.Direction).HasConversion<string>().HasMaxLength(10);
            e.HasIndex(p => p.AccountId);
            e.HasOne<LedgerAccount>().WithMany().HasForeignKey(p => p.AccountId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
