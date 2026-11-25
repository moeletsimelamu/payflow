using PayFlow.Infrastructure.Telemetry;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Workers;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Payments.Application;

/// <summary>
/// Resolves pending payments whose simulated outcome is due. Safe on many instances: each payment is
/// changed under optimistic concurrency, and the ledger's unique (source, entry type) prevents double posting.
/// </summary>
internal sealed class PendingPaymentsJob(IServiceScopeFactory scopes, PayFlowDbContext db, TimeProvider clock, ILogger<PendingPaymentsJob> logger) : IRecurringJob
{
    private static readonly Actor Actor = Actor.System("pending-resolver");

    public string Name => "pending-payments";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var due = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Pending && p.SimulatedResolveAt <= now)
            .OrderBy(p => p.SimulatedResolveAt)
            .Select(p => p.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        var handled = 0;
        foreach (var id in due)
        {
            // One scope per payment, so a failure or conflict never affects the others.
            await using var scope = scopes.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
            var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();
            var payment = await scopedDb.Set<Payment>().SingleAsync(p => p.Id == id, cancellationToken);
            if (payment.Status != PaymentStatus.Pending)
            {
                continue;
            }

            using var activity = PayFlowTelemetry.StartWork("payment.resolve_pending");

            try
            {
                if (await processor.ResolvePendingAsync(payment, PaymentSimulator.ResolvePending(payment.PaymentMethod), Actor, cancellationToken))
                {
                    handled++;
                }
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Payment {PaymentId} was resolved concurrently; skipping.", PublicId.Create(IdPrefixes.Payment, id));
            }
        }

        return handled;
    }
}

/// <summary>
/// Completes payments left in Processing by a request that crashed between its two transactions.
/// The simulator is deterministic, so the outcome is the one the original request would have had,
/// and the stored idempotent response is filled in, so the client's retry replays it.
/// </summary>
internal sealed class ProcessingRecoveryJob(
    IServiceScopeFactory scopes,
    PayFlowDbContext db,
    TimeProvider clock,
    IOptions<WorkerOptions> options,
    ILogger<ProcessingRecoveryJob> logger) : IRecurringJob
{
    private static readonly Actor Actor = Actor.System("processing-recovery");

    public string Name => "processing-recovery";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var cutoff = clock.GetUtcNow() - options.Value.ProcessingTimeout;
        var stuck = await db.Set<Payment>().AsNoTracking()
            .Where(p => p.Status == PaymentStatus.Processing && p.ProcessingStartedAt <= cutoff)
            .OrderBy(p => p.ProcessingStartedAt)
            .Select(p => p.Id)
            .Take(50)
            .ToListAsync(cancellationToken);

        var handled = 0;
        foreach (var id in stuck)
        {
            await using var scope = scopes.CreateAsyncScope();
            var scopedDb = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
            var processor = scope.ServiceProvider.GetRequiredService<PaymentProcessor>();
            var payment = await scopedDb.Set<Payment>().SingleAsync(p => p.Id == id, cancellationToken);
            if (payment.Status != PaymentStatus.Processing)
            {
                continue;
            }

            using var activity = PayFlowTelemetry.StartWork("payment.recover_processing");

            try
            {
                var result = await processor.CompleteProcessingAsync(payment, PaymentSimulator.Decide(payment.PaymentMethod), Actor, cancellationToken);
                if (!result.AppliedHere)
                {
                    continue; // the original request finished it while we waited for the lock
                }

                logger.LogWarning("Recovered payment {PaymentId} stuck in Processing; it is now {Status}.",
                    PublicId.Create(IdPrefixes.Payment, id), payment.Status);
                handled++;
            }
            catch (DbUpdateConcurrencyException)
            {
                logger.LogInformation("Payment {PaymentId} was completed concurrently; skipping.", PublicId.Create(IdPrefixes.Payment, id));
            }
        }

        return handled;
    }
}
