using System.Globalization;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using PayFlow.Api.Logging;
using PayFlow.Api.Middleware;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Results;
using StackExchange.Redis;

namespace PayFlow.Api.Hosting;

public sealed class RateLimitingOptions
{
    public const string Section = "RateLimiting";

    /// <summary>Login attempts per client IP per window.</summary>
    public int AuthenticationPermitLimit { get; set; } = 10;

    public TimeSpan AuthenticationWindow { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>Merchant API requests per API key per window, counted in Redis across all instances.</summary>
    public int MerchantApiPermitLimit { get; set; } = 600;

    public TimeSpan MerchantApiWindow { get; set; } = TimeSpan.FromMinutes(1);
}

public static class ApiSetup
{
    private const string ReadyTag = "ready";

    public static IServiceCollection AddPayFlowApi(this IServiceCollection services, IConfiguration configuration)
    {
        // Strict JSON: unknown properties are rejected so typos in financial requests never pass silently.
        services.ConfigureHttpJsonOptions(o =>
        {
            o.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
            o.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

        services.AddProblemDetails(o => o.CustomizeProblemDetails = ctx =>
        {
            var correlation = ctx.HttpContext.RequestServices.GetService<ICorrelationContext>();
            if (correlation is not null)
            {
                ctx.ProblemDetails.Extensions["correlationId"] = correlation.CorrelationId;
            }

            ctx.ProblemDetails.Instance ??= ctx.HttpContext.Request.Path;
            ctx.ProblemDetails.Extensions.Remove("traceId");
        });
        services.AddExceptionHandler<GlobalExceptionHandler>();

        services.Configure<RateLimitingOptions>(configuration.GetSection(RateLimitingOptions.Section));
        var limits = configuration.GetSection(RateLimitingOptions.Section).Get<RateLimitingOptions>() ?? new RateLimitingOptions();
        services.AddRateLimiter(o =>
        {
            o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            o.AddPolicy(RateLimitPolicies.Authentication, http => RateLimitPartition.GetFixedWindowLimiter(
                partitionKey: http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                factory: _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.AuthenticationPermitLimit,
                    Window = limits.AuthenticationWindow,
                    QueueLimit = 0,
                }));
            o.OnRejected = async (ctx, ct) =>
            {
                if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
                {
                    ctx.HttpContext.Response.Headers.RetryAfter =
                        ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
                }

                await ErrorResults.Problem(new Error("rate_limited", "Too many requests. Retry after the indicated delay.", ErrorKind.TooManyRequests))
                    .ExecuteAsync(ctx.HttpContext);
            };
        });

        services.Configure<ForwardedHeadersOptions>(o =>
        {
            o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            o.KnownIPNetworks.Clear();
            o.KnownProxies.Clear();
            foreach (var cidr in configuration.GetSection("ReverseProxy:TrustedNetworks").Get<string[]>() ?? [])
            {
                o.KnownIPNetworks.Add(System.Net.IPNetwork.Parse(cidr));
            }
        });

        services.AddHealthChecks()
            .AddNpgSql(
                configuration.GetConnectionString(InfrastructureServiceCollectionExtensions.DatabaseConnectionName)!,
                name: "postgres",
                tags: [ReadyTag])
            .AddRedis(sp => sp.GetRequiredService<IConnectionMultiplexer>(), name: "redis", tags: [ReadyTag]);

        return services;
    }

    public static WebApplication UsePayFlowPipeline(this WebApplication app)
    {
        app.UseForwardedHeaders();
        if (!app.Environment.IsDevelopment())
        {
            app.UseHsts();
        }

        app.UseMiddleware<SecurityHeadersMiddleware>();
        app.UseMiddleware<CorrelationIdMiddleware>();
        app.UseSerilogRequestLoggingWithCorrelation();
        app.UseExceptionHandler();
        app.UseStatusCodePages();
        app.UseRouting();
        app.UseRateLimiter();
        app.UseAuthentication();
        app.UseMiddleware<CsrfProtectionMiddleware>();
        app.UseAuthorization();
        return app;
    }

    /// <summary>
    /// The merchant API group: API-key authentication, then the per-key Redis quota. Modules map
    /// their <c>/v1</c> endpoints onto this group.
    /// </summary>
    public static RouteGroupBuilder MapMerchantApiGroup(this WebApplication app) =>
        app.MapGroup("/v1")
            .RequireAuthorization(PayFlow.Modules.Merchants.Contracts.MerchantApiAuth.Policy)
            .AddEndpointFilter<MerchantApiRateLimitFilter>();

    public static WebApplication MapPayFlowHealthChecks(this WebApplication app)
    {
        // Liveness: the process is up. No dependencies, so a database outage never restarts the API.
        app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false }).AllowAnonymous();

        // Readiness: dependencies reachable. Response shows only overall and per-check status.
        app.MapHealthChecks("/health/ready", new HealthCheckOptions
        {
            Predicate = check => check.Tags.Contains(ReadyTag),
            ResultStatusCodes =
            {
                [HealthStatus.Healthy] = StatusCodes.Status200OK,
                [HealthStatus.Degraded] = StatusCodes.Status200OK,
                [HealthStatus.Unhealthy] = StatusCodes.Status503ServiceUnavailable,
            },
        }).AllowAnonymous();
        return app;
    }
}
