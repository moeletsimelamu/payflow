using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace PayFlow.Infrastructure.Telemetry;

/// <summary>
/// PayFlow's own traces and metrics, on the BCL types OpenTelemetry listens to (the API host wires
/// the exporter; modules need no OpenTelemetry package). Tags carry statuses, job names and event
/// types only: never amounts, merchant ids or anything else with unbounded cardinality or that is
/// sensitive.
/// </summary>
public static class PayFlowTelemetry
{
    public const string SourceName = "PayFlow";
    public const string MeterName = "PayFlow";

    public static readonly ActivitySource Source = new(SourceName);

    private static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> PaymentOutcomes = Meter.CreateCounter<long>(
        "payflow.payments.outcomes", "{payment}", "Payments that reached a status after processing or pending resolution, by status.");

    public static readonly Counter<long> RefundOutcomes = Meter.CreateCounter<long>(
        "payflow.refunds.outcomes", "{refund}", "Refunds completed by the refund worker, by status.");

    public static readonly Counter<long> WebhookAttempts = Meter.CreateCounter<long>(
        "payflow.webhooks.attempts", "{attempt}", "Webhook delivery attempts, by outcome (success or the error kind).");

    public static readonly Histogram<double> WebhookAttemptDuration = Meter.CreateHistogram<double>(
        "payflow.webhooks.attempt.duration", "s", "Time from sending a webhook to the endpoint's answer or the failure.");

    public static readonly Histogram<double> WorkerBatchDuration = Meter.CreateHistogram<double>(
        "payflow.worker.batch.duration", "s", "Duration of one background worker batch, by job.");

    public static readonly Counter<long> WorkerItems = Meter.CreateCounter<long>(
        "payflow.worker.items", "{item}", "Items handled by background workers, by job.");

    public static readonly Counter<long> WorkerFailures = Meter.CreateCounter<long>(
        "payflow.worker.failures", "{batch}", "Background worker batches that threw, by job.");

    /// <summary>
    /// Starts the root span of one unit of background work (a webhook delivery, an outbox message, a
    /// refund). Workers poll every second or two; starting spans only once there is real work keeps
    /// idle polls out of the traces.
    /// </summary>
    public static Activity? StartWork(string name, string? correlationId = null)
    {
        var activity = Source.StartActivity(name, ActivityKind.Internal, parentContext: default);
        if (correlationId is not null)
        {
            activity?.SetTag(CorrelationIdTag, correlationId);
        }

        return activity;
    }

    public const string CorrelationIdTag = "payflow.correlation_id";

    public static KeyValuePair<string, object?> Tag(string key, object? value) => new(key, value);
}
