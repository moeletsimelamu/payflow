using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Reconciliation.Domain;

namespace PayFlow.Modules.Reconciliation.Infrastructure;

internal sealed class ReconciliationDbModel : IModuleDbModel
{
    public const string Schema = "reconciliation";

    private const string Outcomes = "'Matched', 'MissingExternal', 'MissingInternal', 'AmountMismatch', 'CurrencyMismatch', 'DuplicateExternal', 'StatusDiscrepancy'";
    private const string ExceptionStatuses = "'Open', 'InReview', 'Resolved', 'Dismissed'";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Statement>(e =>
        {
            e.ToTable("statements", Schema, t =>
            {
                t.HasCheckConstraint("ck_statements_period", "period_end >= period_start");
                t.HasCheckConstraint("ck_statements_status", "status IN ('Imported', 'Reconciled')");
            });
            e.HasKey(s => s.Id);
            e.Property(s => s.SourceName).HasMaxLength(100);
            e.Property(s => s.FileName).HasMaxLength(200);
            e.Property(s => s.ImportedBy).HasMaxLength(100);
            e.Property(s => s.Status).HasConversion<string>().HasMaxLength(20);
            // REC-1: a statement file is imported once.
            e.HasIndex(s => s.FileSha256).IsUnique();
            e.HasIndex(s => s.ImportedAt);
            e.Ignore(s => s.PeriodStartUtc);
            e.Ignore(s => s.PeriodEndExclusiveUtc);
        });

        modelBuilder.Entity<StatementLine>(e =>
        {
            e.ToTable("statement_lines", Schema, t =>
            {
                t.HasCheckConstraint("ck_statement_lines_type", "transaction_type IN ('PAYMENT', 'REFUND')");
                t.HasCheckConstraint("ck_statement_lines_amount", "amount > 0");
            });
            e.HasKey(l => l.Id);
            e.Property(l => l.Id).UseIdentityAlwaysColumn();
            e.Property(l => l.ExternalId).HasMaxLength(100);
            e.Property(l => l.Reference).HasMaxLength(100);
            e.Property(l => l.TransactionType).HasConversion<string>().HasMaxLength(10);
            e.Property(l => l.Currency).HasColumnType("char(3)");
            e.Property(l => l.ExternalStatus).HasMaxLength(20);
            e.Property(l => l.Raw).HasMaxLength(1000);
            e.HasOne<Statement>().WithMany().HasForeignKey(l => l.StatementId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(l => new { l.StatementId, l.LineNumber }).IsUnique();
        });

        modelBuilder.Entity<ReconciliationRun>(e =>
        {
            e.ToTable("runs", Schema);
            e.HasKey(r => r.Id);
            e.Property(r => r.StartedBy).HasMaxLength(100);
            e.HasOne<Statement>().WithMany().HasForeignKey(r => r.StatementId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.StatementId, r.StartedAt });
        });

        modelBuilder.Entity<ReconciliationResult>(e =>
        {
            e.ToTable("results", Schema, t => t.HasCheckConstraint("ck_results_outcome", $"outcome IN ({Outcomes})"));
            e.HasKey(r => r.Id);
            e.Property(r => r.Id).UseIdentityAlwaysColumn();
            e.Property(r => r.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.InternalType).HasMaxLength(10);
            e.Property(r => r.Reference).HasMaxLength(100);
            e.Property(r => r.ExternalId).HasMaxLength(100);
            e.Property(r => r.InternalCurrency).HasColumnType("char(3)");
            e.Property(r => r.ExternalCurrency).HasColumnType("char(3)");
            e.Property(r => r.InternalStatus).HasMaxLength(20);
            e.Property(r => r.ExternalStatus).HasMaxLength(20);
            e.Property(r => r.Detail).HasMaxLength(500);
            e.HasOne<ReconciliationRun>().WithMany().HasForeignKey(r => r.RunId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<StatementLine>().WithMany().HasForeignKey(r => r.StatementLineId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(r => new { r.RunId, r.Outcome });
        });

        modelBuilder.Entity<ReconciliationException>(e =>
        {
            e.ToTable("exceptions", Schema, t =>
            {
                t.HasCheckConstraint("ck_exceptions_outcome", $"outcome IN ({Outcomes}) AND outcome <> 'Matched'");
                t.HasCheckConstraint("ck_exceptions_status", $"status IN ({ExceptionStatuses})");
                t.HasCheckConstraint(
                    "ck_exceptions_resolution",
                    "(status IN ('Resolved', 'Dismissed')) = (resolution_type IS NOT NULL AND resolution_note IS NOT NULL AND resolved_by IS NOT NULL AND resolved_at IS NOT NULL)");
                t.HasCheckConstraint(
                    "ck_exceptions_adjustment",
                    "(resolution_type IS NOT DISTINCT FROM 'AdjustmentPosted') = (adjustment_amount IS NOT NULL AND adjustment_amount > 0 AND adjustment_direction IS NOT NULL)");
                t.HasCheckConstraint("ck_exceptions_currency", "internal_currency IS NOT NULL OR external_currency IS NOT NULL");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Outcome).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.ResolutionType).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.Reference).HasMaxLength(100);
            e.Property(x => x.ExternalId).HasMaxLength(100);
            e.Property(x => x.InternalType).HasMaxLength(10);
            e.Property(x => x.InternalCurrency).HasColumnType("char(3)");
            e.Property(x => x.ExternalCurrency).HasColumnType("char(3)");
            e.Property(x => x.AdjustmentDirection).HasConversion<string>().HasMaxLength(20);
            e.Ignore(x => x.Currency);
            e.Property(x => x.Detail).HasMaxLength(500);
            e.Property(x => x.AssignedTo).HasMaxLength(100);
            e.Property(x => x.ResolutionNote).HasMaxLength(1000);
            e.Property(x => x.ResolvedBy).HasMaxLength(100);
            e.Property(x => x.Version).IsRowVersion();
            // REC-5: reruns update the existing exception, never add another.
            e.HasIndex(x => x.Fingerprint).IsUnique();
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => x.StatementId);
            e.HasOne<Statement>().WithMany().HasForeignKey(x => x.StatementId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<ReconciliationResult>().WithMany().HasForeignKey(x => x.LatestResultId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ExceptionHistoryEntry>(e =>
        {
            e.ToTable("exception_history", Schema);
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).UseIdentityAlwaysColumn();
            e.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(20);
            e.Property(h => h.Note).HasMaxLength(1000);
            e.Property(h => h.Actor).HasMaxLength(100);
            e.HasOne<ReconciliationException>().WithMany().HasForeignKey(h => h.ExceptionId).OnDelete(DeleteBehavior.Restrict);
            e.HasIndex(h => new { h.ExceptionId, h.OccurredAt });
        });
    }
}
