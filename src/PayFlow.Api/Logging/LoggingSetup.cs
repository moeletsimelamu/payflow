using PayFlow.Infrastructure.Correlation;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PayFlow.Api.Logging;

public static class LoggingSetup
{
    /// <summary>Events buffered between request threads and stdout; beyond this, new events are dropped.</summary>
    public const int LogBufferSize = 10_000;

    /// <summary>
    /// Structured JSON logs to stdout with redaction of sensitive values. Writes go through a background
    /// queue: a synchronous console sink blocks request threads whenever the container runtime reads
    /// stdout slowly, and under load that starved the thread pool while database transactions were
    /// open (tests/load: p95 went from 16 s to under 1 s at 80 req/s). Logs are diagnostics, not the
    /// audit trail (that is the append-only audit table), so dropping events when the buffer is full is
    /// preferable to stalling payments.
    /// </summary>
    public static void ConfigureSerilog(HostBuilderContext context, IServiceProvider services, LoggerConfiguration logger) =>
        logger
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "PayFlow.Api")
            .Destructure.With<SensitiveDataDestructuringPolicy>()
            .WriteTo.Async(sink => sink.Console(new RenderedCompactJsonFormatter()), bufferSize: LogBufferSize, blockWhenFull: false);

    /// <summary>
    /// One summary line per request. Only the path is logged: never query strings, headers or bodies,
    /// which may contain credentials.
    /// </summary>
    public static IApplicationBuilder UseSerilogRequestLoggingWithCorrelation(this IApplicationBuilder app) =>
        app.UseSerilogRequestLogging(o =>
        {
            o.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0} ms";
            o.GetLevel = (http, _, ex) =>
                ex is not null || http.Response.StatusCode >= 500 ? LogEventLevel.Error
                : http.Request.Path.StartsWithSegments("/health") ? LogEventLevel.Verbose
                : LogEventLevel.Information;
            o.EnrichDiagnosticContext = (diagnostics, http) =>
            {
                var correlation = http.RequestServices.GetService<ICorrelationContext>();
                if (correlation is not null)
                {
                    diagnostics.Set("CorrelationId", correlation.CorrelationId);
                }
            };
        });
}
