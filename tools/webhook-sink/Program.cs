// PayFlow webhook sink: a reference receiver for local demos (not for production).
//
//   POST /webhooks          receives PayFlow events, verifies the signature, de-duplicates on event id
//   POST /config            { "secret": "whsec_...", "failNext": 3 }  set the signing secret / fail the next N deliveries
//   GET  /events            what was received, newest first (JSON)
//   GET  /                  the same as an HTML table
//
// What a real receiver must do is shown in HandleWebhook: verify against the raw body, reject stale
// timestamps, de-duplicate on the event id, and answer 2xx quickly.

using System.Collections.Concurrent;
using System.Net;
using System.Text;
using PayFlow.Webhooks.Signature;

var builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<SinkState>();
var app = builder.Build();

app.MapPost("/webhooks", HandleWebhook);
app.MapPost("/config", (SinkConfig config, SinkState state) =>
{
    if (!string.IsNullOrWhiteSpace(config.Secret))
    {
        state.Secret = config.Secret.Trim();
    }

    if (config.FailNext is { } n)
    {
        state.FailNext = Math.Max(0, n);
    }

    return Results.Ok(new { secretConfigured = state.Secret is not null, failNext = state.FailNext });
});
app.MapGet("/events", (SinkState state) => Results.Ok(state.Received.Reverse()));
app.MapGet("/health", () => Results.Ok("ok"));
app.MapGet("/", (SinkState state) => Results.Content(Html(state), "text/html; charset=utf-8"));

await app.RunAsync();

static async Task<IResult> HandleWebhook(HttpRequest request, SinkState state)
{
    // Verify against the raw bytes received: re-serialising the JSON would change the signed content.
    using var reader = new StreamReader(request.Body, Encoding.UTF8);
    var body = await reader.ReadToEndAsync();
    var eventId = request.Headers["PayFlow-Event-Id"].ToString();
    var eventType = request.Headers["PayFlow-Event-Type"].ToString();
    var attempt = request.Headers["PayFlow-Delivery-Attempt"].ToString();

    var verification = state.Secret is null
        ? "NoSecretConfigured"
        : WebhookSignature.Verify(body, request.Headers[WebhookSignature.HeaderName].ToString(), state.Secret, DateTimeOffset.UtcNow).ToString();

    if (state.TryConsumeFailure())
    {
        state.Record(new ReceivedEvent(eventId, eventType, attempt, verification, Duplicate: false, DateTimeOffset.UtcNow, 500));
        return Results.StatusCode(500);
    }

    if (state.Secret is not null && verification != nameof(SignatureVerification.Valid))
    {
        state.Record(new ReceivedEvent(eventId, eventType, attempt, verification, Duplicate: false, DateTimeOffset.UtcNow, 400));
        return Results.BadRequest(new { error = verification });
    }

    // Delivery is at least once: the same event can arrive again (retry after a timeout, manual redelivery).
    var duplicate = !state.Seen.TryAdd(eventId, 0);
    state.Record(new ReceivedEvent(eventId, eventType, attempt, verification, duplicate, DateTimeOffset.UtcNow, 200));
    return Results.Ok(new { received = eventId, duplicate });
}

static string Html(SinkState state)
{
    var rows = string.Join("", state.Received.Reverse().Select(e =>
        $"<tr><td>{e.ReceivedAt:HH:mm:ss}</td><td>{WebUtility.HtmlEncode(e.EventType)}</td><td><code>{WebUtility.HtmlEncode(e.EventId)}</code></td>" +
        $"<td>{WebUtility.HtmlEncode(e.Attempt)}</td><td>{e.Verification}</td><td>{(e.Duplicate ? "yes" : "")}</td><td>{e.ResponseStatus}</td></tr>"));
    return $$"""
        <!doctype html><html><head><meta charset="utf-8"><title>PayFlow webhook sink</title>
        <meta http-equiv="refresh" content="3">
        <style>body{font-family:system-ui;margin:2rem}table{border-collapse:collapse}td,th{border:1px solid #ccc;padding:.3rem .6rem;text-align:left}</style>
        </head><body><h1>PayFlow webhook sink</h1>
        <p>Secret configured: {{(state.Secret is null ? "no" : "yes")}} · failing next: {{state.FailNext}} · simulated events only</p>
        <table><tr><th>Received</th><th>Type</th><th>Event id</th><th>Attempt</th><th>Signature</th><th>Duplicate</th><th>Answered</th></tr>{{rows}}</table>
        </body></html>
        """;
}

internal sealed record SinkConfig(string? Secret, int? FailNext);

internal sealed record ReceivedEvent(string EventId, string EventType, string Attempt, string Verification, bool Duplicate, DateTimeOffset ReceivedAt, int ResponseStatus);

internal sealed class SinkState
{
    private int _failNext;

    public string? Secret { get; set; } = Environment.GetEnvironmentVariable("SINK_SECRET");

    public int FailNext
    {
        get => Volatile.Read(ref _failNext);
        set => Volatile.Write(ref _failNext, value);
    }

    public ConcurrentDictionary<string, byte> Seen { get; } = new(StringComparer.Ordinal);

    public ConcurrentQueue<ReceivedEvent> Received { get; } = new();

    public bool TryConsumeFailure()
    {
        while (true)
        {
            var current = Volatile.Read(ref _failNext);
            if (current <= 0)
            {
                return false;
            }

            if (Interlocked.CompareExchange(ref _failNext, current - 1, current) == current)
            {
                return true;
            }
        }
    }

    public void Record(ReceivedEvent e)
    {
        Received.Enqueue(e);
        while (Received.Count > 500 && Received.TryDequeue(out _))
        {
        }
    }
}
