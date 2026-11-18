using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Settlement.Domain;

namespace PayFlow.Modules.Settlement.Infrastructure;

internal sealed class SettlementDbModel : IModuleDbModel
{
    public const string Schema = "settlement";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<SettlementBatch>(e =>
        {
            e.ToTable("batches", Schema, t =>
            {
                t.HasCheckConstraint("ck_batches_status", "status IN ('Pending', 'Paid', 'Failed')");
                t.HasCheckConstraint("ck_batches_net", "net_amount = gross_payments - total_fees - total_refunds + carried_forward_in");
                t.HasCheckConstraint("ck_batches_payout", "payout_amount >= 0 AND carried_forward_out <= 0 AND carried_forward_in <= 0 AND payout_amount + carried_forward_out = net_amount");
                t.HasCheckConstraint("ck_batches_counts", "payment_count >= 0 AND refund_count >= 0 AND payment_count + refund_count > 0");
            });
            e.HasKey(b => b.Id);
            e.Property(b => b.Currency).HasColumnType("char(3)");
            e.Property(b => b.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(b => b.CreatedBy).HasMaxLength(100);
            e.Property(b => b.PayoutReference).HasMaxLength(60);
            e.Property(b => b.FailureReason).HasMaxLength(300);
            e.Property(b => b.Version).IsRowVersion();
            e.HasMany(b => b.Items).WithOne().HasForeignKey(i => i.BatchId);
            e.Navigation(b => b.Items).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasIndex(b => new { b.MerchantId, b.Currency, b.CreatedAt });
            // One open (unpaid) batch per merchant and currency keeps the carry-forward chain linear.
            e.HasIndex(b => new { b.MerchantId, b.Currency }).IsUnique().HasFilter("status <> 'Paid'").HasDatabaseName("ux_batches_one_open");
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(b => b.Currency).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SettlementItem>(e =>
        {
            e.ToTable("items", Schema, t => t.HasCheckConstraint("ck_items_type", "item_type IN ('Payment', 'Refund')"));
            e.HasKey(i => i.Id);
            e.Property(i => i.Id).UseIdentityAlwaysColumn();
            e.Property(i => i.ItemType).HasConversion<string>().HasMaxLength(10);
            // Rule S5: a payment or refund can be settled exactly once, whatever the application does.
            e.HasIndex(i => new { i.ItemType, i.SourceId }).IsUnique();
            e.HasIndex(i => i.PaymentId);
        });
    }
}
