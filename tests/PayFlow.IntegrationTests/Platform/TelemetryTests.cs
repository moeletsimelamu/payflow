using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Net.Http.Json;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Telemetry;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Platform;

/// <summary>
/// OpenTelemetry (docs/REQUIREMENTS.md §5): requests and background work produce spans that carry the
/// correlation id end to end, and PayFlow's meter reports payment outcomes. Listens with the BCL
/// listeners OpenTelemetry itself uses, so no exporter is needed. Other tests run in parallel, so
/// everything is matched on this test's own correlation id.
/// </summary>
public sealed class TelemetryTests(PayFlowTestHost host)
{
    [Fact]
    public async Task A_payment_request_and_the_background_work_it_causes_share_its_correlation_id()
    {
        using var spans = new SpanRecorder("Microsoft.AspNetCore", PayFlowTelemetry.SourceName);
        using var outcomes = new MeasurementRecorder("payflow.payments.outcomes");
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var correlationId = $"telemetry-{Guid.NewGuid():N}";

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/payments")
        {
            Content = JsonContent.Create(PaymentScenario.Body("12.00", method: "sim_card_declined_do_not_honour")),
        };
        request.Headers.Add("Idempotency-Key", Guid.NewGuid().ToString("N"));
        request.Headers.Add("X-Correlation-Id", correlationId);
        using var response = await m.Api.SendAsync(request);
        ((int)response.StatusCode).ShouldBe(201);

        // The request span carries the correlation id and the payment's outcome.
        var server = spans.Single(a => a.Kind == ActivityKind.Server && Tagged(a, PayFlowTelemetry.CorrelationIdTag, correlationId));
        server.GetTagItem("payflow.payment.status").ShouldBe("Declined");
        outcomes.Values("payflow.payment.status").ShouldContain("Declined");

        // The outbox message raised by the payment is dispatched in its own trace, under the same id.
        for (var i = 0; i < 5 && !spans.Any(a => a.OperationName == "outbox.dispatch" && Tagged(a, PayFlowTelemetry.CorrelationIdTag, correlationId)); i++)
        {
            await RefundScenario.RunJobAsync<OutboxDispatchJob>(host.Factory);
        }

        var work = spans.Single(a => a.OperationName == "outbox.dispatch" && Tagged(a, PayFlowTelemetry.CorrelationIdTag, correlationId));
        work.GetTagItem("payflow.message_type").ShouldBe("payment.declined");
        work.ParentId.ShouldBeNull(); // a root span of its own, not a child of whatever ran before
        work.TraceId.ShouldNotBe(server.TraceId);
    }

    private static bool Tagged(Activity a, string key, string value) => Equals(a.GetTagItem(key), value);

    private sealed class SpanRecorder : IDisposable
    {
        private readonly ConcurrentQueue<Activity> _stopped = new();
        private readonly ActivityListener _listener;

        public SpanRecorder(params string[] sources)
        {
            _listener = new ActivityListener
            {
                ShouldListenTo = s => sources.Contains(s.Name),
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = _stopped.Enqueue,
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public Activity Single(Func<Activity, bool> predicate) => _stopped.Where(predicate).ShouldHaveSingleItem();

        public bool Any(Func<Activity, bool> predicate) => _stopped.Any(predicate);

        public void Dispose() => _listener.Dispose();
    }

    private sealed class MeasurementRecorder : IDisposable
    {
        private readonly ConcurrentQueue<KeyValuePair<string, object?>[]> _tags = new();
        private readonly MeterListener _listener = new();

        public MeasurementRecorder(string instrument)
        {
            _listener.InstrumentPublished = (i, l) =>
            {
                if (i.Meter.Name == PayFlowTelemetry.MeterName && i.Name == instrument)
                {
                    l.EnableMeasurementEvents(i);
                }
            };
            _listener.SetMeasurementEventCallback<long>((_, _, tags, _) => _tags.Enqueue(tags.ToArray()));
            _listener.Start();
        }

        public IReadOnlyList<object?> Values(string tag) =>
            _tags.SelectMany(t => t).Where(kv => kv.Key == tag).Select(kv => kv.Value).ToList();

        public void Dispose() => _listener.Dispose();
    }
}
