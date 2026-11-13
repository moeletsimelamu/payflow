using System.Text.Json;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.SharedKernel.Ids;

namespace PayFlow.Infrastructure.Outbox;

/// <summary>
/// A message recorded in the same transaction as the business change that produced it, and
/// dispatched afterwards by a background worker. This is how PayFlow guarantees an event is
/// published if and only if the change committed (docs/ARCHITECTURE.md §6).
/// </summary>
public sealed class OutboxMessage
{
    public Guid Id { get; init; }

    public required string Type { get; init; }

    /// <summary>JSON payload (jsonb).</summary>
    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public required string CorrelationId { get; init; }

    public DateTimeOffset? ProcessedAt { get; set; }

    public int Attempts { get; set; }

    public string? LastError { get; set; }
}

/// <summary>Enqueues integration messages into the current unit of work. Nothing is sent until the caller commits.</summary>
public interface IOutbox
{
    void Enqueue<TPayload>(string type, TPayload payload);
}

internal sealed class EfOutbox(PayFlowDbContext db, IdGenerator ids, TimeProvider clock, ICorrelationContext correlation) : IOutbox
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public void Enqueue<TPayload>(string type, TPayload payload)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = ids.NewId(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload, JsonOptions),
            OccurredAt = clock.GetUtcNow(),
            CorrelationId = correlation.CorrelationId,
        });
    }
}
