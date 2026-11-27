using PayFlow.Infrastructure.Telemetry;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Security;
using PayFlow.Infrastructure.Workers;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.Modules.Webhooks.Domain;
using PayFlow.Modules.Webhooks.Infrastructure;
using PayFlow.SharedKernel.Ids;
using PayFlow.Webhooks.Signature;

namespace PayFlow.Modules.Webhooks.Application;

/// <summary>
/// Sends due deliveries (WH-3..5).
/// <list type="number">
/// <item>Claim: in a short transaction, push <c>next_attempt_at</c> out by the lease for a batch of due
/// rows locked with <c>SKIP LOCKED</c>. Other senders skip them; no transaction stays open during HTTP.</item>
/// <item>Send each with a fresh signature and timestamp, outside any transaction.</item>
/// <item>Record the attempt and the new delivery state in one transaction.</item>
/// </list>
/// A crash after sending but before recording means the receiver may see the event again after the
/// lease expires: delivery is at least once, and receivers de-duplicate on the event id (WH-6).
/// </summary>
internal sealed class WebhookDeliveryJob(
    IServiceScopeFactory scopes,
    PayFlowDbContext db,
    TimeProvider clock,
    IOptions<WebhookOptions> options,
    ILogger<WebhookDeliveryJob> logger) : IRecurringJob
{
    public const string HttpClientName = "payflow-webhooks";
    private const int BatchSize = 20;
    private const int MaxBodyExcerpt = 2048;

    public string Name => "webhook-delivery";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        var leaseUntil = now + options.Value.ClaimLease;
        List<Guid> claimed;
        await using (var transaction = await db.Database.BeginTransactionAsync(cancellationToken))
        {
            claimed = await db.Database.SqlQuery<Guid>(
                    $"""
                    UPDATE webhooks.deliveries SET next_attempt_at = {leaseUntil}
                     WHERE id IN (SELECT id FROM webhooks.deliveries
                                   WHERE status IN ('Pending', 'Retrying') AND next_attempt_at <= {now}
                                   ORDER BY next_attempt_at
                                   LIMIT {BatchSize}
                                     FOR UPDATE SKIP LOCKED)
                    RETURNING id AS "Value"
                    """)
                .ToListAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        foreach (var id in claimed)
        {
            await using var scope = scopes.CreateAsyncScope();
            await DeliverAsync(scope.ServiceProvider, id, cancellationToken);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(IServiceProvider sp, Guid deliveryId, CancellationToken ct)
    {
        var scopedDb = sp.GetRequiredService<PayFlowDbContext>();
        var delivery = await scopedDb.Set<WebhookDelivery>().SingleAsync(d => d.Id == deliveryId, ct);
        var endpoint = await scopedDb.Set<WebhookEndpoint>().AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);
        var webhookEvent = await scopedDb.Set<WebhookEvent>().AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        var correlation = sp.GetRequiredService<CorrelationContext>();
        var started = clock.GetUtcNow();
        var attemptNumber = delivery.NextAttemptNumber;
        using var activity = PayFlowTelemetry.StartWork("webhook.deliver", correlation.CorrelationId);
        activity?.SetTag("payflow.event_type", webhookEvent.Type);
        activity?.SetTag("payflow.attempt", attemptNumber);

        if (!endpoint.IsEnabled)
        {
            delivery.Cancel(started);
            Record(scopedDb, delivery, attemptNumber, started, 0, "{}", null, null, DeliveryErrorKind.EndpointDisabled, "Endpoint disabled; delivery cancelled.");
            await scopedDb.SaveChangesAsync(ct);
            return;
        }

        var protector = sp.GetRequiredService<SecretProtector>();
        var secrets = endpoint.SigningSecretCiphertexts(started).Select(c => protector.Unprotect(c, WebhookSecrets.Purpose)).ToList();
        var timestamp = started.ToUnixTimeSeconds();
        var eventId = PublicId.Create(IdPrefixes.Event, webhookEvent.Id);

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint.Url)
        {
            Content = new StringContent(webhookEvent.Payload, Encoding.UTF8, "application/json"),
        };
        request.Headers.UserAgent.Add(new ProductInfoHeaderValue("PayFlow-Webhooks", "1.0"));
        request.Headers.Add(WebhookSignature.HeaderName, WebhookSignature.CreateHeader(timestamp, webhookEvent.Payload, secrets));
        request.Headers.Add(WebhookHeaders.EventId, eventId);
        request.Headers.Add(WebhookHeaders.EventType, webhookEvent.Type);
        request.Headers.Add(WebhookHeaders.DeliveryId, delivery.Id.ToString("N"));
        request.Headers.Add(WebhookHeaders.Attempt, attemptNumber.ToString(CultureInfo.InvariantCulture));
        request.Headers.Add(CorrelationContext.HeaderName, correlation.CorrelationId);
        var loggedHeaders = RedactedHeaders(request, timestamp);

        int? status = null;
        string? body = null;
        DeliveryErrorKind? error = null;
        string? errorMessage = null;
        var stopwatch = Stopwatch.StartNew();
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(options.Value.RequestTimeout);
            var client = sp.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName);
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
            status = (int)response.StatusCode;
            body = await ReadExcerptAsync(response, timeout.Token);
            if (!response.IsSuccessStatusCode)
            {
                error = DeliveryErrorKind.Non2xx;
                errorMessage = $"Endpoint answered {status}.";
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            error = DeliveryErrorKind.Timeout;
            errorMessage = $"No response within {options.Value.RequestTimeout.TotalSeconds:0} s.";
        }
        catch (HttpRequestException ex) when (FindSsrf(ex) is { } ssrf)
        {
            error = DeliveryErrorKind.SsrfBlocked;
            errorMessage = ssrf.Message;
        }
        catch (HttpRequestException ex)
        {
            error = DeliveryErrorKind.ConnectionFailed;
            errorMessage = Truncate(ex.Message, 500);
        }

        var finished = clock.GetUtcNow();
        var outcome = PayFlowTelemetry.Tag("payflow.outcome", error?.ToString() ?? "Success");
        PayFlowTelemetry.WebhookAttempts.Add(1, outcome);
        PayFlowTelemetry.WebhookAttemptDuration.Record(stopwatch.Elapsed.TotalSeconds, outcome);
        if (error is not null)
        {
            activity?.SetStatus(ActivityStatusCode.Error, error.ToString());
        }

        if (error is null)
        {
            delivery.RecordSuccess(finished);
        }
        else
        {
            delivery.RecordFailure(finished, Random.Shared.Next(-20, 21));
        }

        Record(scopedDb, delivery, attemptNumber, started, (long)stopwatch.Elapsed.TotalMilliseconds, loggedHeaders, status, body, error, errorMessage);
        await scopedDb.SaveChangesAsync(ct);
        logger.LogInformation("Webhook {EventId} attempt {Attempt} to endpoint {EndpointId}: {Outcome}; delivery is {Status}.",
            eventId, attemptNumber, PublicId.Create(IdPrefixes.WebhookEndpoint, endpoint.Id), error?.ToString() ?? $"HTTP {status}", delivery.Status);
    }

    private static void Record(PayFlowDbContext db, WebhookDelivery delivery, int attempt, DateTimeOffset started, long durationMs, string headers, int? status, string? body, DeliveryErrorKind? error, string? message) =>
        db.Set<DeliveryAttempt>().Add(new DeliveryAttempt
        {
            DeliveryId = delivery.Id,
            AttemptNumber = attempt,
            StartedAt = started,
            DurationMs = durationMs,
            RequestHeaders = headers,
            ResponseStatus = status,
            ResponseBodyExcerpt = body,
            ErrorKind = error,
            ErrorMessage = message,
        });

    /// <summary>Headers as sent, except the signature values, so attempt logs never contain a usable signature.</summary>
    private static string RedactedHeaders(HttpRequestMessage request, long timestamp)
    {
        var headers = request.Headers.Concat(request.Content!.Headers)
            .ToDictionary(h => h.Key, h => string.Join(", ", h.Value), StringComparer.OrdinalIgnoreCase);
        headers[WebhookSignature.HeaderName] = string.Create(CultureInfo.InvariantCulture, $"t={timestamp},v1=***");
        return JsonSerializer.Serialize(headers);
    }

    private static async Task<string?> ReadExcerptAsync(HttpResponseMessage response, CancellationToken ct)
    {
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new char[MaxBodyExcerpt];
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var read = await reader.ReadBlockAsync(buffer.AsMemory(), ct);
        return read == 0 ? null : new string(buffer, 0, read);
    }

    private static SsrfBlockedException? FindSsrf(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is SsrfBlockedException ssrf)
            {
                return ssrf;
            }
        }

        return null;
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
