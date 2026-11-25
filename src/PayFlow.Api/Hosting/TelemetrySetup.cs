using System.Diagnostics;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using PayFlow.Infrastructure.Telemetry;

namespace PayFlow.Api.Hosting;

/// <summary>
/// OpenTelemetry traces and metrics (docs/REQUIREMENTS.md §5, Observability). Spans: every HTTP
/// request (health probes excluded), every unit of background work (PayFlowTelemetry.StartWork), and
/// the database and outbound HTTP calls made inside them. Metrics: PayFlow's own meter, ASP.NET Core,
/// HttpClient, Npgsql and the .NET runtime. Logs stay structured JSON on stdout and carry the trace
/// and span ids, so they join up with the traces.
///
/// Export is OTLP and switched on by the standard <c>OTEL_EXPORTER_OTLP_ENDPOINT</c> variable;
/// without it the instrumentation runs but nothing leaves the process.
/// </summary>
public static class TelemetrySetup
{
    public const string ServiceName = "payflow-api";
    public const string OtlpEndpointKey = "OTEL_EXPORTER_OTLP_ENDPOINT";

    /// <summary>
    /// Share of new traces kept (0–1). A payment request makes about 40 database calls, each a span:
    /// at 100 req/s, full sampling with export pushed POST /v1/payments p95 from ~70 ms to over
    /// 500 ms on the reference machine (tests/load). Unsampled requests create no spans at all.
    /// Metrics are unaffected: they count every request.
    /// </summary>
    public const string SampleRatioKey = "Telemetry:TraceSampleRatio";

    public static IServiceCollection AddPayFlowTelemetry(this IServiceCollection services, IConfiguration configuration)
    {
        var ratio = Math.Clamp(configuration.GetValue(SampleRatioKey, 1.0), 0.0, 1.0);
        var telemetry = services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(ServiceName, serviceVersion: typeof(TelemetrySetup).Assembly.GetName().Version?.ToString()))
            .WithTracing(t => t
                .SetSampler(new ParentBasedSampler(new DropRootClientSpans(new TraceIdRatioBasedSampler(ratio))))
                .AddSource(PayFlowTelemetry.SourceName)
                // Query strings are redacted by default; exceptions (which may quote data) are not recorded.
                .AddAspNetCoreInstrumentation(o => o.Filter = http => !http.Request.Path.StartsWithSegments("/health", StringComparison.OrdinalIgnoreCase))
                .AddHttpClientInstrumentation()
                // Spans carry the parameterised SQL text, never parameter values.
                .AddNpgsql())
            .WithMetrics(m => m
                .AddMeter(PayFlowTelemetry.MeterName)
                .AddMeter("Npgsql")
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation());

        if (!string.IsNullOrWhiteSpace(configuration[OtlpEndpointKey]))
        {
            telemetry.UseOtlpExporter();
        }

        return services;
    }

    /// <summary>
    /// Root sampler: drops client spans that have no parent. Those are the background workers'
    /// polling queries (several a second, mostly finding nothing); the work they find gets its own
    /// root span, and its queries are sampled as its children. Other roots go to <paramref name="next"/>.
    /// </summary>
    internal sealed class DropRootClientSpans(Sampler next) : Sampler
    {
        private static readonly SamplingResult Drop = new(SamplingDecision.Drop);

        public override SamplingResult ShouldSample(in SamplingParameters samplingParameters) =>
            samplingParameters.Kind == ActivityKind.Client ? Drop : next.ShouldSample(samplingParameters);
    }
}
