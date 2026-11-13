using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Webhooks.Domain;

namespace PayFlow.Modules.Webhooks.Infrastructure;

internal sealed class WebhooksDbModel : IModuleDbModel
{
    public const string Schema = "webhooks";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<WebhookEndpoint>(e =>
        {
            e.ToTable("endpoints", Schema, t => t.HasCheckConstraint("ck_endpoints_events", "cardinality(event_types) > 0"));
            e.HasKey(x => x.Id);
            e.Property(x => x.Url).HasMaxLength(2048);
            e.Property(x => x.Description).HasMaxLength(200);
            e.Property(x => x.SecretCiphertext).HasMaxLength(500);
            e.Property(x => x.SecretHint).HasMaxLength(4);
            e.Property(x => x.PreviousSecretCiphertext).HasMaxLength(500);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => x.MerchantId);
        });

        modelBuilder.Entity<WebhookEvent>(e =>
        {
            e.ToTable("events", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Type).HasMaxLength(60);
            e.Property(x => x.ApiVersion).HasMaxLength(20);
            e.Property(x => x.Payload).HasColumnType("text"); // exact bytes that are signed and sent
            e.HasIndex(x => x.SourceMessageId).IsUnique();
            e.HasIndex(x => new { x.MerchantId, x.CreatedAt });
        });

        modelBuilder.Entity<WebhookDelivery>(e =>
        {
            e.ToTable("deliveries", Schema, t =>
            {
                t.HasCheckConstraint("ck_deliveries_status", "status IN ('Pending', 'Retrying', 'Succeeded', 'Exhausted', 'Cancelled')");
                t.HasCheckConstraint("ck_deliveries_attempts", "attempt_count >= 0 AND attempt_budget_start <= attempt_count");
            });
            e.HasKey(x => x.Id);
            e.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);
            e.Property(x => x.Version).IsRowVersion();
            e.HasIndex(x => new { x.EventId, x.EndpointId }).IsUnique();
            e.HasIndex(x => x.NextAttemptAt).HasFilter("status IN ('Pending', 'Retrying')").HasDatabaseName("ix_deliveries_due");
            e.HasIndex(x => new { x.MerchantId, x.CreatedAt });
            e.HasOne<WebhookEvent>().WithMany().HasForeignKey(x => x.EventId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne<WebhookEndpoint>().WithMany().HasForeignKey(x => x.EndpointId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<DeliveryAttempt>(e =>
        {
            e.ToTable("delivery_attempts", Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.RequestHeaders).HasColumnType("jsonb");
            e.Property(x => x.ResponseBodyExcerpt).HasMaxLength(2048);
            e.Property(x => x.ErrorKind).HasConversion<string>().HasMaxLength(30);
            e.Property(x => x.ErrorMessage).HasMaxLength(500);
            e.HasIndex(x => new { x.DeliveryId, x.AttemptNumber }).IsUnique();
            e.HasOne<WebhookDelivery>().WithMany().HasForeignKey(x => x.DeliveryId).OnDelete(DeleteBehavior.Restrict);
        });
    }
}
