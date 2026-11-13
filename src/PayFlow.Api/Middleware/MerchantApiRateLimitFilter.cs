using System.Globalization;
using Microsoft.Extensions.Options;
using PayFlow.Api.Hosting;
using PayFlow.Infrastructure.Http;
using PayFlow.Infrastructure.RateLimiting;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Api.Middleware;

/// <summary>Per-API-key request quota on the merchant API, shared across instances via Redis.</summary>
public sealed class MerchantApiRateLimitFilter(RedisRateLimiter limiter, ICurrentMerchant merchant, IOptions<RateLimitingOptions> options) : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);
        if (merchant.ApiKeyId is not { } keyId)
        {
            return await next(context);
        }

        var settings = options.Value;
        var decision = await limiter.AcquireAsync($"key:{keyId:N}", settings.MerchantApiPermitLimit, settings.MerchantApiWindow);
        var headers = context.HttpContext.Response.Headers;
        headers["RateLimit-Limit"] = decision.Limit.ToString(CultureInfo.InvariantCulture);
        headers["RateLimit-Remaining"] = decision.Remaining.ToString(CultureInfo.InvariantCulture);

        if (decision.Allowed)
        {
            return await next(context);
        }

        headers.RetryAfter = Math.Max(1, (int)Math.Ceiling(decision.RetryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);
        return ErrorResults.Problem(new Error("rate_limited", "API key request quota exceeded. Retry after the indicated delay.", ErrorKind.TooManyRequests));
    }
}
