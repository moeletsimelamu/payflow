using System.Net;

namespace PayFlow.Modules.Audit.Domain;

/// <summary>An immutable audit record. The table is append-only at the database level.</summary>
internal sealed class AuditEvent
{
    public long Id { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public required string ActorType { get; init; }

    public string? ActorId { get; init; }

    public required string ActorDisplay { get; init; }

    public Guid? MerchantId { get; init; }

    public required string Action { get; init; }

    public required string TargetType { get; init; }

    public string? TargetId { get; init; }

    /// <summary>Redacted JSON (jsonb).</summary>
    public string? Changes { get; init; }

    public IPAddress? IpAddress { get; init; }

    public string? UserAgent { get; init; }

    public required string CorrelationId { get; init; }
}
