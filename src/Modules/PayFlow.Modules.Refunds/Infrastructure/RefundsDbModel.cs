using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Domain;

namespace PayFlow.Modules.Refunds.Infrastructure;

internal sealed class RefundsDbModel : IModuleDbModel
{
    public const string Schema = "refunds";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Refund>(e =>
        {
            var statuses = string.Join(", ", Enum.GetNames<RefundStatus>().Select(s => $"'{s}'"));
            e.ToTable("refunds", Schema, t =>
            {
                t.HasCheckConstraint("ck_refunds_amount", "amount > 0");
                t.HasCheckConstraint("ck_refunds_status", $"status IN ({statuses})");
                t.HasCheckConstraint("ck_refunds_failure_reason", "status <> 'Failed' OR failure_reason IS NOT NULL");
                t.HasCheckConstraint("ck_refunds_completed", "(status = 'Pending') = (completed_at IS NULL)");
            });
            e.HasKey(r => r.Id);
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(r => r.Reason).HasMaxLength(300);
            e.Property(r => r.FailureReason).HasMaxLength(300);
            e.Property(r => r.PaymentMethod).HasMaxLength(60);
            e.Property(r => r.RequestedByType).HasMaxLength(20);
            e.Property(r => r.RequestedById).HasMaxLength(100);
            e.Property(r => r.IdempotencyKey).HasMaxLength(255);
            e.Property(r => r.Version).IsRowVersion();
            e.HasIndex(r => new { r.MerchantId, r.IdempotencyKey }).IsUnique();
            e.HasIndex(r => new { r.PaymentId, r.CreatedAt });
            e.HasIndex(r => r.CreatedAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_refunds_pending");
            e.HasIndex(r => new { r.MerchantId, r.Currency, r.CompletedAt }).HasFilter("settlement_batch_id IS NULL AND status = 'Succeeded'").HasDatabaseName("ix_refunds_unsettled");
            e.HasIndex(r => r.CompletedAt);
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(r => r.Currency).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Reversal>(e =>
        {
            e.ToTable("reversals", Schema, t =>
            {
                t.HasCheckConstraint("ck_reversals_amount", "amount > 0");
                t.HasCheckConstraint("ck_reversals_fee", "fee_returned >= 0 AND fee_returned < amount");
            });
            e.HasKey(r => r.Id);
            e.Property(r => r.Currency).HasColumnType("char(3)");
            e.Property(r => r.Reason).HasMaxLength(300);
            e.Property(r => r.RequestedByType).HasMaxLength(20);
            e.Property(r => r.RequestedById).HasMaxLength(100);
            e.Property(r => r.IdempotencyKey).HasMaxLength(255);
            e.HasIndex(r => r.PaymentId).IsUnique(); // at most one reversal per payment
            e.HasIndex(r => new { r.MerchantId, r.IdempotencyKey }).IsUnique();
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(r => r.Currency).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
