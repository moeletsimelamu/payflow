using Microsoft.AspNetCore.Antiforgery;
using PayFlow.Infrastructure.Http;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Api.Middleware;

/// <summary>
/// Double-submit CSRF protection for the cookie-authenticated dashboard API (<c>/api</c>).
/// Every state-changing request, including login, must echo the XSRF token in <c>X-XSRF-TOKEN</c>.
/// The merchant API (<c>/v1</c>) uses bearer API keys, which browsers never attach automatically,
/// so it is exempt.
/// </summary>
public sealed class CsrfProtectionMiddleware(RequestDelegate next, IAntiforgery antiforgery, ILogger<CsrfProtectionMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        // Session and antiforgery cookies are Secure-only, so the dashboard API only works over HTTPS
        // (directly, or via a TLS-terminating proxy that sends X-Forwarded-Proto).
        if (context.Request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) && !context.Request.IsHttps)
        {
            await ErrorResults.Problem(Error.Validation("https_required", "The dashboard API is only available over HTTPS."))
                .ExecuteAsync(context);
            return;
        }

        if (RequiresValidation(context.Request))
        {
            try
            {
                await antiforgery.ValidateRequestAsync(context);
            }
            catch (AntiforgeryValidationException)
            {
                logger.LogWarning("Rejected {Method} {Path}: missing or invalid CSRF token.", context.Request.Method, context.Request.Path);
                await ErrorResults.Problem(Error.Forbidden("csrf_token_invalid", "The request is missing a valid XSRF token. Fetch /api/auth/antiforgery and retry."))
                    .ExecuteAsync(context);
                return;
            }
        }

        await next(context);
    }

    private static bool RequiresValidation(HttpRequest request) =>
        request.Path.StartsWithSegments("/api", StringComparison.OrdinalIgnoreCase) &&
        !(HttpMethods.IsGet(request.Method) || HttpMethods.IsHead(request.Method) ||
          HttpMethods.IsOptions(request.Method) || HttpMethods.IsTrace(request.Method));
}
