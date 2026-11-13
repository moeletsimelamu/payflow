using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Audit.Domain;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Audit.Application;

internal sealed class EfAuditLog(
    PayFlowDbContext db,
    IActorContext actorContext,
    ICorrelationContext correlation,
    TimeProvider clock) : IAuditLog
{
    public void Record(AuditEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var actor = entry.Actor ?? actorContext.Actor;
        db.Set<AuditEvent>().Add(new AuditEvent
        {
            OccurredAt = clock.GetUtcNow(),
            ActorType = actor.Type.ToString(),
            ActorId = actor.Id,
            ActorDisplay = Truncate(actor.Display, 200),
            MerchantId = entry.MerchantId,
            Action = entry.Action,
            TargetType = entry.TargetType,
            TargetId = entry.TargetId,
            Changes = entry.Changes is null ? null : RedactingJson.Serialize(entry.Changes),
            IpAddress = actorContext.IpAddress,
            UserAgent = actorContext.UserAgent is null ? null : Truncate(actorContext.UserAgent, 512),
            CorrelationId = correlation.CorrelationId,
        });
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
