using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Modules.Webhooks;

namespace PayFlow.IntegrationTests.Infrastructure;

public sealed record ReceivedWebhook(Uri Url, IReadOnlyDictionary<string, string> Headers, string Body);

/// <summary>
/// Stands in for merchants' receivers: records every request and answers with programmable status
/// codes per host. Tests use a unique host per test, so leftovers from other tests are ignored.
/// </summary>
public sealed class StubWebhookReceiver : HttpMessageHandler
{
    private readonly ConcurrentQueue<ReceivedWebhook> _received = new();
    private readonly ConcurrentDictionary<string, ConcurrentQueue<HttpStatusCode>> _scripted = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, HttpStatusCode> _defaults = new(StringComparer.OrdinalIgnoreCase);

    public static string NewHost() => $"m{Guid.NewGuid():N}.merchant.test";

    /// <summary>Next responses for <paramref name="host"/>, in order; afterwards the host default applies.</summary>
    public void Script(string host, params HttpStatusCode[] statuses)
    {
        var queue = _scripted.GetOrAdd(host, _ => new ConcurrentQueue<HttpStatusCode>());
        foreach (var status in statuses)
        {
            queue.Enqueue(status);
        }
    }

    public void Default(string host, HttpStatusCode status) => _defaults[host] = status;

    public IReadOnlyList<ReceivedWebhook> For(string host) => _received.Where(r => string.Equals(r.Url.Host, host, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Routes the webhook HTTP client of a test factory to this stub.</summary>
    public void Register(IServiceCollection services) =>
        services.AddHttpClient(WebhooksModule.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => this)
            .SetHandlerLifetime(Timeout.InfiniteTimeSpan); // the factory must never dispose the shared stub

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var headers = request.Headers.Concat(request.Content?.Headers ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);
        var body = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        _received.Enqueue(new ReceivedWebhook(request.RequestUri!, headers, body));

        var host = request.RequestUri!.Host;
        var status = _scripted.TryGetValue(host, out var queue) && queue.TryDequeue(out var scripted)
            ? scripted
            : _defaults.GetValueOrDefault(host, HttpStatusCode.OK);
        return new HttpResponseMessage(status) { Content = new StringContent($"{{\"stub\":{(int)status}}}") };
    }
}
