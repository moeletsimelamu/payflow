using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;

namespace PayFlow.Modules.Payments.Infrastructure;

internal sealed class PaymentsDbModel : IModuleDbModel
{
    public const string Schema = "payments";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Payment>(e =>
        {
            var statuses = string.Join(", ", Enum.GetNames<PaymentStatus>().Select(s => $"'{s}'"));
            e.ToTable("payments", Schema, t =>
            {
                t.HasCheckConstraint("ck_payments_amount", "amount > 0");
                t.HasCheckConstraint("ck_payments_status", $"status IN ({statuses})");
                t.HasCheckConstraint("ck_payments_refund_balance",
                    "refunded_amount >= 0 AND pending_refund_amount >= 0 AND refunded_amount + pending_refund_amount <= amount");
                t.HasCheckConstraint("ck_payments_fee", "fee_amount IS NULL OR (fee_amount >= 0 AND fee_amount < amount)");
                t.HasCheckConstraint("ck_payments_fee_when_captured",
                    "status NOT IN ('Succeeded', 'PartiallyRefunded', 'Refunded', 'Reversed') OR fee_amount IS NOT NULL");
                t.HasCheckConstraint("ck_payments_failure_reason",
                    "status NOT IN ('Declined', 'Failed', 'Expired') OR failure_code IS NOT NULL");
                t.HasCheckConstraint("ck_payments_fee_rate", "fee_rate >= 0 AND fee_rate < 1");
            });
            e.HasKey(p => p.Id);
            e.Property(p => p.MerchantReference).HasMaxLength(100);
            e.Property(p => p.Currency).HasColumnType("char(3)");
            e.Property(p => p.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(p => p.PaymentMethod).HasMaxLength(60);
            e.Property(p => p.FailureCode).HasMaxLength(60);
            e.Property(p => p.FailureReason).HasMaxLength(300);
            e.Property(p => p.FeeRate).HasPrecision(7, 6);
            e.Property(p => p.IdempotencyKey).HasMaxLength(255);
            e.Property(p => p.Description).HasMaxLength(500);
            e.Property(p => p.Metadata).HasColumnType("jsonb");
            e.Property(p => p.Version).IsRowVersion();
            e.Ignore(p => p.AmountMoney);
            e.Ignore(p => p.FeeMoney);
            e.HasIndex(p => new { p.MerchantId, p.IdempotencyKey }).IsUnique();
            e.HasIndex(p => new { p.MerchantId, p.CreatedAt }).IsDescending(false, true);
            e.HasIndex(p => new { p.MerchantId, p.Status });
            e.HasIndex(p => new { p.MerchantId, p.MerchantReference });
            e.HasIndex(p => p.SimulatedResolveAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_payments_pending_due");
            e.HasIndex(p => p.ProcessingStartedAt).HasFilter("status = 'Processing'").HasDatabaseName("ix_payments_processing");
            e.HasIndex(p => new { p.Status, p.FailureCode });
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(p => p.Currency).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(p => p.History).WithOne().HasForeignKey(h => h.PaymentId);
            e.Navigation(p => p.History).UsePropertyAccessMode(PropertyAccessMode.Field);
        });

        modelBuilder.Entity<PaymentStatusChange>(e =>
        {
            e.ToTable("payment_status_history", Schema);
            e.HasKey(h => h.Id);
            e.Property(h => h.Id).UseIdentityAlwaysColumn();
            e.Property(h => h.FromStatus).HasConversion<string>().HasMaxLength(30);
            e.Property(h => h.ToStatus).HasConversion<string>().HasMaxLength(30);
            e.Property(h => h.Reason).HasMaxLength(300);
            e.Property(h => h.ActorType).HasMaxLength(20);
            e.Property(h => h.ActorId).HasMaxLength(100);
            e.Property(h => h.CorrelationId).HasMaxLength(64);
            e.HasIndex(h => new { h.PaymentId, h.OccurredAt });
        });
    }
}
