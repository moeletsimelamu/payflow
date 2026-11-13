using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Workers;

namespace PayFlow.Infrastructure.Outbox;

/// <summary>Consumes outbox messages. Runs inside the dispatcher's transaction, so its writes commit atomically with marking the message processed.</summary>
public interface IOutboxHandler
{
    /// <summary>Stable name, recorded in the inbox table for de-duplication.</summary>
    string Name { get; }

    bool Handles(string messageType);

    Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken);
}

/// <summary>Row of <c>platform.inbox_processed</c>: handler <see cref="Handler"/> has processed <see cref="MessageId"/>.</summary>
public sealed class InboxRecord
{
    public Guid MessageId { get; init; }

    public required string Handler { get; init; }

    public DateTimeOffset ProcessedAt { get; init; }
}

/// <summary>
/// Delivers committed outbox messages to their handlers (docs/ARCHITECTURE.md §6).
/// <para>
/// Each message is claimed in its own transaction with <c>FOR UPDATE SKIP LOCKED</c>, so several
/// dispatchers (instances or threads) never process the same message concurrently. Each handler
/// first inserts an inbox row (<c>ON CONFLICT DO NOTHING</c>), so even a message processed twice
/// after a crash is handled at most once per handler. Handler work, the inbox row and
/// <c>processed_at</c> commit together. A failing message is retried on later ticks and set aside
/// after <see cref="MaxAttempts"/> failures.
/// </para>
/// </summary>
public sealed class OutboxDispatchJob(IServiceScopeFactory scopes, ILogger<OutboxDispatchJob> logger) : IRecurringJob
{
    public const int MaxAttempts = 10;
    private const int BatchSize = 100;

    public string Name => "outbox-dispatch";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var failed = new List<Guid>();
        var handled = 0;
        for (var i = 0; i < BatchSize; i++)
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var db = sp.GetRequiredService<PayFlowDbContext>();
            var clock = sp.GetRequiredService<TimeProvider>();

            Guid? claimedId = null;
            try
            {
                await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
                var excluded = failed.ToArray();
                claimedId = (await db.Database.SqlQuery<Guid>(
                        $"""
                        SELECT id AS "Value" FROM platform.outbox_messages
                         WHERE processed_at IS NULL AND attempts < {MaxAttempts} AND NOT (id = ANY({excluded}))
                         ORDER BY occurred_at
                         LIMIT 1
                           FOR UPDATE SKIP LOCKED
                        """)
                    .ToListAsync(cancellationToken)).Cast<Guid?>().FirstOrDefault();
                if (claimedId is null)
                {
                    break;
                }

                var message = await db.OutboxMessages.SingleAsync(m => m.Id == claimedId, cancellationToken);
                sp.GetRequiredService<CorrelationContext>().CorrelationId = message.CorrelationId; // keep the originating request's id

                foreach (var handler in sp.GetServices<IOutboxHandler>().Where(h => h.Handles(message.Type)))
                {
                    var firstTime = await db.Database.ExecuteSqlAsync(
                        $"INSERT INTO platform.inbox_processed (message_id, handler, processed_at) VALUES ({message.Id}, {handler.Name}, {clock.GetUtcNow()}) ON CONFLICT DO NOTHING",
                        cancellationToken);
                    if (firstTime == 1)
                    {
                        await handler.HandleAsync(message, cancellationToken);
                    }
                }

                message.ProcessedAt = clock.GetUtcNow();
                message.Attempts++;
                await db.SaveChangesAsync(cancellationToken);
                await transaction.CommitAsync(cancellationToken);
                handled++;
            }
            catch (Exception ex) when (claimedId is not null && ex is not OperationCanceledException)
            {
                failed.Add(claimedId.Value);
                logger.LogError(ex, "Outbox message {MessageId} failed; it will be retried.", claimedId);
                await RecordFailureAsync(claimedId.Value, ex, cancellationToken);
            }
        }

        return handled;
    }

    private async Task RecordFailureAsync(Guid messageId, Exception ex, CancellationToken ct)
    {
        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
        var error = ex.GetType().Name + ": " + ex.Message;
        error = error.Length > 1000 ? error[..1000] : error;
        await db.Database.ExecuteSqlAsync(
            $"UPDATE platform.outbox_messages SET attempts = attempts + 1, last_error = {error} WHERE id = {messageId}", ct);
    }
}
