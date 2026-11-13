using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Outbox;
using PayFlow.SharedKernel.Money;

namespace PayFlow.Infrastructure.Persistence;

/// <summary>
/// Contributes a module's entity mappings to the shared <see cref="PayFlowDbContext"/>.
/// Each module maps only into its own PostgreSQL schema (docs/ARCHITECTURE.md §3).
/// </summary>
public interface IModuleDbModel
{
    void Configure(ModelBuilder modelBuilder);
}

/// <summary>A dashboard user. Merchant access is granted through memberships, not global roles.</summary>
public sealed class PayFlowUser : IdentityUser<Guid>
{
    public required string DisplayName { get; set; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? LastLoginAt { get; set; }
}

public sealed class PayFlowRole : IdentityRole<Guid>
{
}

/// <summary>Reference data row for <c>platform.currencies</c>, seeded from <see cref="Currency.All"/>.</summary>
public sealed class CurrencyRecord
{
    public required string Code { get; init; }

    public int MinorUnits { get; init; }

    public required string DisplayName { get; init; }

    public bool IsEnabled { get; init; }
}

/// <summary>
/// The single EF Core context for the modular monolith. One context lets a business change, its
/// ledger postings, audit record and outbox message commit in one database transaction
/// (ADR-002). Module isolation is enforced in code by architecture tests.
/// </summary>
public sealed class PayFlowDbContext(DbContextOptions<PayFlowDbContext> options, IEnumerable<IModuleDbModel> modules)
    : IdentityDbContext<PayFlowUser, PayFlowRole, Guid>(options), IDataProtectionKeyContext
{
    public const string PlatformSchema = "platform";
    public const string IdentitySchema = "identity";

    private readonly IModuleDbModel[] _modules = modules.ToArray();

    public DbSet<DataProtectionKey> DataProtectionKeys => Set<DataProtectionKey>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<CurrencyRecord> Currencies => Set<CurrencyRecord>();

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        // Rule M1: every monetary column is numeric(19,4). Nothing else in the model uses decimal.
        configurationBuilder.Properties<decimal>().HavePrecision(19, 4);
    }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);
        ConfigureIdentity(builder);
        ConfigurePlatform(builder);
        foreach (var module in _modules)
        {
            module.Configure(builder);
        }
    }

    private static void ConfigureIdentity(ModelBuilder b)
    {
        b.Entity<PayFlowUser>(e =>
        {
            e.ToTable("users", IdentitySchema);
            e.Property(u => u.DisplayName).HasMaxLength(200);
        });
        b.Entity<PayFlowRole>().ToTable("roles", IdentitySchema);
        b.Entity<IdentityUserRole<Guid>>().ToTable("user_roles", IdentitySchema);
        b.Entity<IdentityUserClaim<Guid>>().ToTable("user_claims", IdentitySchema);
        b.Entity<IdentityUserLogin<Guid>>().ToTable("user_logins", IdentitySchema);
        b.Entity<IdentityUserToken<Guid>>().ToTable("user_tokens", IdentitySchema);
        b.Entity<IdentityRoleClaim<Guid>>().ToTable("role_claims", IdentitySchema);
        b.Entity<DataProtectionKey>().ToTable("data_protection_keys", IdentitySchema);
    }

    private static void ConfigurePlatform(ModelBuilder b)
    {
        b.Entity<CurrencyRecord>(e =>
        {
            e.ToTable("currencies", PlatformSchema, t => t.HasCheckConstraint("ck_currencies_minor_units", "minor_units BETWEEN 0 AND 4"));
            e.HasKey(c => c.Code);
            e.Property(c => c.Code).HasColumnType("char(3)");
            e.Property(c => c.DisplayName).HasMaxLength(100);
            e.HasData(Currency.All.Select(c => new CurrencyRecord
            {
                Code = c.Code,
                MinorUnits = c.MinorUnits,
                DisplayName = c.DisplayName,
                IsEnabled = true,
            }));
        });

        b.Entity<Idempotency.IdempotencyRecord>(e =>
        {
            e.ToTable("idempotency_records", PlatformSchema, t =>
                t.HasCheckConstraint("ck_idempotency_records_status", "status IN ('InProgress', 'Completed')"));
            e.HasKey(r => new { r.Scope, r.Operation, r.Key });
            e.Property(r => r.Scope).HasMaxLength(80);
            e.Property(r => r.Operation).HasMaxLength(60);
            e.Property(r => r.Key).HasMaxLength(255);
            e.Property(r => r.Status).HasMaxLength(20);
            e.Property(r => r.ResourceType).HasMaxLength(40);
            e.HasIndex(r => new { r.ResourceType, r.ResourceId });
            e.HasIndex(r => r.ExpiresAt);
        });

        b.Entity<InboxRecord>(e =>
        {
            e.ToTable("inbox_processed", PlatformSchema);
            e.HasKey(r => new { r.MessageId, r.Handler });
            e.Property(r => r.Handler).HasMaxLength(100);
        });

        b.Entity<OutboxMessage>(e =>
        {
            e.ToTable("outbox_messages", PlatformSchema);
            e.HasKey(m => m.Id);
            e.Property(m => m.Type).HasMaxLength(200);
            e.Property(m => m.Payload).HasColumnType("jsonb");
            e.Property(m => m.CorrelationId).HasMaxLength(64);
            e.Property(m => m.LastError).HasMaxLength(1000);
            e.HasIndex(m => m.OccurredAt)
                .HasDatabaseName("ix_outbox_messages_unprocessed")
                .HasFilter("processed_at IS NULL");
        });
    }
}
