using PayFlow.Infrastructure.Correlation;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace PayFlow.Api.Logging;

public static class LoggingSetup
{
    /// <summary>Structured JSON logs to stdout with redaction of sensitive values.</summary>
    public static void ConfigureSerilog(HostBuilderContext context, IServiceProvider services, LoggerConfiguration logger) =>
        logger
            .ReadFrom.Configuration(context.Configuration)
            .ReadFrom.Services(services)
            .MinimumLevel.Override("Microsoft.AspNetCore", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.EntityFrameworkCore.Database.Command", LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Application", "PayFlow.Api")
            .Destructure.With<SensitiveDataDestructuringPolicy>()
            .WriteTo.Console(new RenderedCompactJsonFormatter());

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
