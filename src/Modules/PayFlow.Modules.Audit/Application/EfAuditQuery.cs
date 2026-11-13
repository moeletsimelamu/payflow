using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Audit.Domain;

namespace PayFlow.Modules.Audit.Application;

internal sealed class EfAuditQuery(PayFlowDbContext db) : IAuditQuery
{
    public async Task<IReadOnlyList<AuditRecord>> ListForMerchantAsync(Guid merchantId, long? beforeId, int limit, CancellationToken cancellationToken)
    {
        var query = db.Set<AuditEvent>().AsNoTracking().Where(e => e.MerchantId == merchantId);
        if (beforeId is { } before)
        {
            query = query.Where(e => e.Id < before);
        }

        return await query
            .OrderByDescending(e => e.Id)
            .Take(Math.Clamp(limit, 1, 100))
            .Select(e => new AuditRecord(e.Id, e.OccurredAt, e.ActorType, e.ActorId, e.ActorDisplay, e.Action, e.TargetType, e.TargetId, e.Changes, e.CorrelationId))
            .ToListAsync(cancellationToken);
    }
}
