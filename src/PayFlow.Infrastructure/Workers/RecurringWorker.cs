using PayFlow.Infrastructure.Telemetry;
using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Idempotency;

namespace PayFlow.Infrastructure.Workers;

public sealed class WorkerOptions
{
    public const string Section = "Workers";

    /// <summary>Integration tests turn the timers off and run each job explicitly.</summary>
    public bool Enabled { get; set; } = true;

    public TimeSpan PendingPaymentsInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan ProcessingRecoveryInterval { get; set; } = TimeSpan.FromSeconds(15);

    public TimeSpan RefundProcessingInterval { get; set; } = TimeSpan.FromSeconds(2);

    public TimeSpan OutboxDispatchInterval { get; set; } = TimeSpan.FromSeconds(1);

    public TimeSpan WebhookDeliveryInterval { get; set; } = TimeSpan.FromSeconds(2);

    /// <summary>A payment still in Processing after this is presumed abandoned by a crashed request.</summary>
    public TimeSpan ProcessingTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public TimeSpan IdempotencySweepInterval { get; set; } = TimeSpan.FromHours(1);
}

/// <summary>A unit of background work. Implementations must be safe to run on several instances at once.</summary>
public interface IRecurringJob
{
    string Name { get; }

    /// <summary>Processes one batch and returns how many items it handled.</summary>
    Task<int> RunOnceAsync(CancellationToken cancellationToken);
}

/// <summary>Runs <typeparamref name="TJob"/> on a timer, in a fresh scope with its own correlation id each time.</summary>
public sealed class RecurringWorker<TJob>(
    IServiceScopeFactory scopes,
    IOptions<WorkerOptions> options,
    Func<WorkerOptions, TimeSpan> interval,
    ILogger<RecurringWorker<TJob>> logger) : BackgroundService
    where TJob : IRecurringJob
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            return;
        }

        using var timer = new PeriodicTimer(interval(options.Value));
        var jobTag = PayFlowTelemetry.Tag("payflow.job", typeof(TJob).Name);
        do
        {
            var started = Stopwatch.GetTimestamp();
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                scope.ServiceProvider.GetRequiredService<CorrelationContext>().CorrelationId = CorrelationContext.NewId();
                var job = scope.ServiceProvider.GetRequiredService<TJob>();
                var handled = await job.RunOnceAsync(stoppingToken);
                PayFlowTelemetry.WorkerBatchDuration.Record(Stopwatch.GetElapsedTime(started).TotalSeconds, jobTag);
                if (handled > 0)
                {
                    PayFlowTelemetry.WorkerItems.Add(handled, jobTag);
                    logger.LogInformation("{Job} handled {Count} item(s).", job.Name, handled);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // A failing batch must not stop the worker; the next tick retries.
                PayFlowTelemetry.WorkerFailures.Add(1, jobTag);
                logger.LogError(ex, "{Job} failed; retrying on the next tick.", typeof(TJob).Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}

/// <summary>Deletes idempotency records past their retention.</summary>
public sealed class IdempotencySweepJob(IdempotencyStore store) : IRecurringJob
{
    public string Name => "idempotency-sweep";

    public Task<int> RunOnceAsync(CancellationToken cancellationToken) => store.SweepExpiredAsync(cancellationToken);
}

public static class WorkerRegistration
{
    public static IServiceCollection AddRecurringJob<TJob>(this IServiceCollection services, Func<WorkerOptions, TimeSpan> interval)
        where TJob : class, IRecurringJob
    {
        services.AddScoped<TJob>();
        services.AddHostedService(sp => new RecurringWorker<TJob>(
            sp.GetRequiredService<IServiceScopeFactory>(),
            sp.GetRequiredService<IOptions<WorkerOptions>>(),
            interval,
            sp.GetRequiredService<ILogger<RecurringWorker<TJob>>>()));
        return services;
    }
}
