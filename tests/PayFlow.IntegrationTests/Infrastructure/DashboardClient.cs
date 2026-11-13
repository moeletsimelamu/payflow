using System.Net.Http.Json;
using System.Text.Json;
using PayFlow.Modules.Identity.Contracts;

namespace PayFlow.IntegrationTests.Infrastructure;

/// <summary>
/// Behaves like the Angular app: keeps cookies, reads the XSRF-TOKEN cookie and echoes it in the
/// X-XSRF-TOKEN header on state-changing requests.
/// </summary>
public sealed class DashboardClient(HttpClient http) : IDisposable
{
    private string? _xsrfToken;

    public HttpClient Http => http;

    public bool HasXsrfToken => _xsrfToken is not null;

    public async Task PrimeAntiforgeryAsync()
    {
        var response = await http.GetAsync(new Uri("/api/auth/antiforgery", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        Capture(response);
    }

    public Task<HttpResponseMessage> PostAsync(string url, object? body, bool sendXsrf = true, string? correlationId = null) =>
        SendAsync(HttpMethod.Post, url, body, sendXsrf, correlationId);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, object? body, bool sendXsrf = true, string? correlationId = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        if (sendXsrf && _xsrfToken is not null)
        {
            request.Headers.Add(AuthCookies.XsrfHeader, _xsrfToken);
        }

        if (correlationId is not null)
        {
            request.Headers.Add("X-Correlation-Id", correlationId);
        }

        var response = await http.SendAsync(request);
        Capture(response);
        return response;
    }

    /// <summary>A state-changing dashboard request carrying an Idempotency-Key, as the SPA sends for money movements.</summary>
    public async Task<HttpResponseMessage> PostIdempotentAsync(string url, object body, string? idempotencyKey = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (_xsrfToken is not null)
        {
            request.Headers.Add(AuthCookies.XsrfHeader, _xsrfToken);
        }

        request.Headers.Add("Idempotency-Key", idempotencyKey ?? Guid.NewGuid().ToString("N"));
        var response = await http.SendAsync(request);
        Capture(response);
        return response;
    }

    public async Task<HttpResponseMessage> LoginAsync(string email, string password, string? correlationId = null)
    {
        if (!HasXsrfToken)
        {
            await PrimeAntiforgeryAsync();
        }

        return await PostAsync("/api/auth/login", new { email, password }, correlationId: correlationId);
    }

    public Task<HttpResponseMessage> LoginAsAdminAsync() => LoginAsync(PayFlowTestHost.AdminEmail, PayFlowTestHost.AdminPassword);

    public static async Task<JsonElement> ReadJsonAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.Clone();
    }

    public void Dispose() => http.Dispose();

    private void Capture(HttpResponseMessage response)
    {
        if (!response.Headers.TryGetValues("Set-Cookie", out var cookies))
        {
            return;
        }

        const string prefix = AuthCookies.XsrfToken + "=";
        foreach (var cookie in cookies.Where(c => c.StartsWith(prefix, StringComparison.Ordinal)))
        {
            var value = cookie.Split(';')[0][prefix.Length..];
            _xsrfToken = value.Length == 0 ? null : Uri.UnescapeDataString(value);
        }
    }
}
