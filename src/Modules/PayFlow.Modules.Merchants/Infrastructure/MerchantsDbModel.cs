using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;

namespace PayFlow.Modules.Merchants.Infrastructure;

internal sealed class MerchantsDbModel : IModuleDbModel
{
    public const string Schema = "merchants";

    private static readonly DateTimeOffset SeedTime = new(2026, 11, 12, 0, 0, 0, TimeSpan.Zero);

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<FeePlan>(e =>
        {
            e.ToTable("fee_plans", Schema, t => t.HasCheckConstraint("ck_fee_plans_rate", "percentage_rate >= 0 AND percentage_rate < 1"));
            e.HasKey(p => p.Id);
            e.Property(p => p.Name).HasMaxLength(100);
            e.Property(p => p.PercentageRate).HasPrecision(7, 6);
            e.HasIndex(p => new { p.Name, p.Version }).IsUnique();
            e.HasMany(p => p.Terms).WithOne().HasForeignKey(t => t.FeePlanId);
            e.Navigation(p => p.Terms).UsePropertyAccessMode(PropertyAccessMode.Field);

            var standard = FeePlan.StandardV1(SeedTime);
            e.HasData(new { standard.Id, standard.Name, standard.Version, standard.PercentageRate, standard.CreatedAt });
        });

        modelBuilder.Entity<FeePlanCurrencyTerms>(e =>
        {
            e.ToTable("fee_plan_currency_terms", Schema, t =>
            {
                t.HasCheckConstraint("ck_fee_plan_terms_fixed_fee", "fixed_fee >= 0");
                t.HasCheckConstraint("ck_fee_plan_terms_minimum", "min_payment_amount > fixed_fee");
            });
            e.HasKey(t => new { t.FeePlanId, t.Currency });
            e.Property(t => t.Currency).HasColumnType("char(3)");
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(t => t.Currency).OnDelete(DeleteBehavior.Restrict);
            e.HasData(FeePlan.StandardV1(SeedTime).Terms.Select(t => new { t.FeePlanId, t.Currency, t.FixedFee, t.MinPaymentAmount }));
        });

        modelBuilder.Entity<Merchant>(e =>
        {
            var statuses = string.Join(", ", Enum.GetNames<MerchantStatus>().Select(s => $"'{s}'"));
            e.ToTable("merchants", Schema, t => t.HasCheckConstraint("ck_merchants_status", $"status IN ({statuses})"));
            e.HasKey(m => m.Id);
            e.Property(m => m.LegalName).HasMaxLength(200);
            e.Property(m => m.TradingName).HasMaxLength(200);
            e.Property(m => m.ContactEmail).HasMaxLength(256);
            e.Property(m => m.CountryCode).HasColumnType("char(2)");
            e.Property(m => m.Status).HasConversion<string>().HasMaxLength(30);
            e.Property(m => m.StatusReason).HasMaxLength(500);
            e.Property(m => m.Version).IsRowVersion();
            e.HasOne<FeePlan>().WithMany().HasForeignKey(m => m.FeePlanId).OnDelete(DeleteBehavior.Restrict);
            e.HasMany(m => m.Currencies).WithOne().HasForeignKey(c => c.MerchantId);
            e.Navigation(m => m.Currencies).UsePropertyAccessMode(PropertyAccessMode.Field);
            e.HasIndex(m => m.Status);
            e.HasIndex(m => m.CreatedAt);
        });

        modelBuilder.Entity<MerchantCurrency>(e =>
        {
            e.ToTable("merchant_currencies", Schema, t =>
            {
                t.HasCheckConstraint("ck_merchant_currencies_max", "max_payment_amount > 0");
                t.HasCheckConstraint("ck_merchant_currencies_refund_limit", "operator_refund_limit IS NULL OR operator_refund_limit >= 0");
            });
            e.HasKey(c => new { c.MerchantId, c.Currency });
            e.Property(c => c.Currency).HasColumnType("char(3)");
            e.HasOne<CurrencyRecord>().WithMany().HasForeignKey(c => c.Currency).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.ToTable("api_keys", Schema, t =>
            {
                t.HasCheckConstraint("ck_api_keys_prefix", "length(prefix) = 8");
                t.HasCheckConstraint("ck_api_keys_hash", "length(secret_hash) = 32");
            });
            e.HasKey(k => k.Id);
            e.Property(k => k.Name).HasMaxLength(100);
            e.Property(k => k.Prefix).HasMaxLength(8);
            e.Property(k => k.CreatedBy).HasMaxLength(100);
            e.HasIndex(k => k.Prefix).IsUnique();
            e.HasIndex(k => k.MerchantId);
            e.HasOne<Merchant>().WithMany().HasForeignKey(k => k.MerchantId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
