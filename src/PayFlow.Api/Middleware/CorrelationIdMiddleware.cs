using PayFlow.Infrastructure.Correlation;
using Serilog.Context;

namespace PayFlow.Api.Middleware;

/// <summary>
/// Accepts a safe client-supplied <c>X-Correlation-Id</c> or generates one, then exposes it to
/// logs, the response, audit records and outbox messages.
/// </summary>
public sealed class CorrelationIdMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, CorrelationContext correlation)
    {
        var supplied = context.Request.Headers[CorrelationContext.HeaderName].ToString();
        correlation.CorrelationId = CorrelationContext.IsValid(supplied) ? supplied : CorrelationContext.NewId();

        context.Response.OnStarting(() =>
        {
            context.Response.Headers[CorrelationContext.HeaderName] = correlation.CorrelationId;
            return Task.CompletedTask;
        });

        using (LogContext.PushProperty("CorrelationId", correlation.CorrelationId))
        {
            await next(context);
        }
    }
}
