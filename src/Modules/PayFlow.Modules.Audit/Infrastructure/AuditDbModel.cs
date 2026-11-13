using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Domain;

namespace PayFlow.Modules.Audit.Infrastructure;

internal sealed class AuditDbModel : IModuleDbModel
{
    public const string Schema = "audit";
    public const string EventsTable = "audit_events";

    public void Configure(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<AuditEvent>(e =>
        {
            e.ToTable(EventsTable, Schema);
            e.HasKey(x => x.Id);
            e.Property(x => x.Id).UseIdentityAlwaysColumn();
            e.Property(x => x.ActorType).HasMaxLength(20);
            e.Property(x => x.ActorId).HasMaxLength(100);
            e.Property(x => x.ActorDisplay).HasMaxLength(200);
            e.Property(x => x.Action).HasMaxLength(100);
            e.Property(x => x.TargetType).HasMaxLength(50);
            e.Property(x => x.TargetId).HasMaxLength(100);
            e.Property(x => x.Changes).HasColumnType("jsonb");
            e.Property(x => x.IpAddress).HasColumnType("inet");
            e.Property(x => x.UserAgent).HasMaxLength(512);
            e.Property(x => x.CorrelationId).HasMaxLength(64);
            e.HasIndex(x => new { x.MerchantId, x.OccurredAt }).IsDescending(false, true);
            e.HasIndex(x => new { x.TargetType, x.TargetId });
            e.HasIndex(x => x.OccurredAt);
        });
    }
}
