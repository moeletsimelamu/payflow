namespace PayFlow.Modules.Audit.Contracts;

/// <summary>A stored audit event as exposed to other modules. <see cref="Changes"/> is already redacted JSON.</summary>
public sealed record AuditRecord(
    long Id,
    DateTimeOffset OccurredAt,
    string ActorType,
    string? ActorId,
    string ActorDisplay,
    string Action,
    string TargetType,
    string? TargetId,
    string? Changes,
    string CorrelationId);

/// <summary>Read access to the audit trail (merchant activity history, MER-7).</summary>
public interface IAuditQuery
{
    /// <summary>Newest first. Pass the smallest id of the previous page as <paramref name="beforeId"/> to continue.</summary>
    Task<IReadOnlyList<AuditRecord>> ListForMerchantAsync(Guid merchantId, long? beforeId, int limit, CancellationToken cancellationToken);
}
