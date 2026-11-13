namespace PayFlow.Api.Middleware;

/// <summary>
/// Defensive headers for API responses. The Angular app's own CSP is set by nginx, which serves it.
/// Financial responses are never cached by browsers or intermediaries.
/// </summary>
public sealed class SecurityHeadersMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            var headers = context.Response.Headers;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=(), payment=()";
            headers.CacheControl = "no-store";
            headers.Remove("Server");
            return Task.CompletedTask;
        });

        return next(context);
    }
}
