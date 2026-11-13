using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Security;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.Modules.Webhooks.Domain;
using PayFlow.Modules.Webhooks.Infrastructure;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Webhooks.Application;

public sealed record CreateWebhookEndpointRequest(string Url, string? Description, IReadOnlyList<string> EventTypes);

public sealed record UpdateWebhookEndpointRequest(string? Url, string? Description, IReadOnlyList<string>? EventTypes, bool? IsEnabled);

public sealed record WebhookEndpointResponse(
    string Id,
    string Url,
    string? Description,
    IReadOnlyList<string> EventTypes,
    bool IsEnabled,
    string SecretHint,
    DateTimeOffset? PreviousSecretExpiresAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Returned on creation and on secret roll only. The secret is never shown again.</summary>
public sealed record WebhookSecretResponse(WebhookEndpointResponse Endpoint, [property: Sensitive] string Secret, string Notice);

public sealed record DeliveryAttemptResponse(
    int AttemptNumber, DateTimeOffset StartedAt, long DurationMs, int? ResponseStatus, string? ResponseBodyExcerpt, DeliveryErrorKind? ErrorKind, string? ErrorMessage, JsonElement RequestHeaders);

public sealed record DeliveryResponse(
    string Id,
    string EventId,
    string EventType,
    string EndpointId,
    string EndpointUrl,
    DeliveryStatus Status,
    int AttemptCount,
    DateTimeOffset? NextAttemptAt,
    DateTimeOffset? LastAttemptAt,
    DateTimeOffset? SucceededAt,
    DateTimeOffset CreatedAt,
    IReadOnlyList<DeliveryAttemptResponse>? Attempts);

public sealed record DeliveryPage(IReadOnlyList<DeliveryResponse> Items, int Page, int PageSize, int Total);

public sealed record EventResponse(string Id, [property: JsonPropertyName("object")] string ObjectType, string Type, DateTimeOffset CreatedAt, JsonElement Data);

public sealed record EventPage(IReadOnlyList<EventResponse> Data, bool HasMore, string? NextStartingAfter);

internal static class WebhookSecrets
{
    public const string Purpose = "PayFlow.Webhooks.SigningSecret";

    public static string Generate() => "whsec_" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
}

/// <summary>Endpoint management (WH-1), delivery history (WH-5), redelivery (WH-4) and test pings.</summary>
internal sealed class WebhookService(
    PayFlowDbContext db,
    SecretProtector protector,
    WebhookEventFactory events,
    IAuditLog audit,
    IOptions<WebhookOptions> options,
    IdGenerator ids,
    TimeProvider clock)
{
    private const string Notice = "Store this signing secret now. PayFlow shows it only once.";
    private const string Target = "webhook_endpoint";

    public async Task<IReadOnlyList<WebhookEndpointResponse>> ListEndpointsAsync(Guid merchantId, CancellationToken ct) =>
        (await db.Set<WebhookEndpoint>().AsNoTracking().Where(e => e.MerchantId == merchantId).OrderBy(e => e.CreatedAt).ToListAsync(ct))
            .Select(ToResponse).ToList();

    public async Task<Result<WebhookSecretResponse>> CreateEndpointAsync(Guid merchantId, CreateWebhookEndpointRequest request, CancellationToken ct)
    {
        var url = SsrfGuard.ValidateUrl(request.Url, options.Value);
        if (!url.IsSuccess)
        {
            return url.Error;
        }

        var types = ValidateEventTypes(request.EventTypes);
        if (!types.IsSuccess)
        {
            return types.Error;
        }

        if (await db.Set<WebhookEndpoint>().CountAsync(e => e.MerchantId == merchantId, ct) >= options.Value.MaxEndpointsPerMerchant)
        {
            return Error.Conflict("webhook_endpoint_limit_reached", $"A merchant can have at most {options.Value.MaxEndpointsPerMerchant} webhook endpoints.");
        }

        var secret = WebhookSecrets.Generate();
        var endpoint = WebhookEndpoint.Create(ids.NewId(), merchantId, url.Value.ToString(), request.Description?.Trim(), types.Value,
            protector.Protect(secret, WebhookSecrets.Purpose), secret[^4..], clock.GetUtcNow());
        db.Set<WebhookEndpoint>().Add(endpoint);
        audit.Record(new AuditEntry("merchant.webhook_endpoint.created", Target, PublicId.Create(IdPrefixes.WebhookEndpoint, endpoint.Id))
        {
            MerchantId = merchantId,
            Changes = new { endpoint.Url, endpoint.EventTypes },
        });
        await db.SaveChangesAsync(ct);
        return new WebhookSecretResponse(ToResponse(endpoint), secret, Notice);
    }

    public async Task<Result<WebhookEndpointResponse>> UpdateEndpointAsync(Guid merchantId, Guid endpointId, UpdateWebhookEndpointRequest request, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(merchantId, endpointId, ct);
        if (endpoint is null)
        {
            return EndpointNotFound();
        }

        string? url = null;
        if (request.Url is not null)
        {
            var validated = SsrfGuard.ValidateUrl(request.Url, options.Value);
            if (!validated.IsSuccess)
            {
                return validated.Error;
            }

            url = validated.Value.ToString();
        }

        IReadOnlyList<string>? types = null;
        if (request.EventTypes is not null)
        {
            var validated = ValidateEventTypes(request.EventTypes);
            if (!validated.IsSuccess)
            {
                return validated.Error;
            }

            types = validated.Value;
        }

        var before = new { endpoint.Url, endpoint.EventTypes, endpoint.IsEnabled };
        endpoint.Update(url, request.Description?.Trim(), types, request.IsEnabled, clock.GetUtcNow());
        audit.Record(new AuditEntry("merchant.webhook_endpoint.updated", Target, PublicId.Create(IdPrefixes.WebhookEndpoint, endpoint.Id))
        {
            MerchantId = merchantId,
            Changes = new { before, after = new { endpoint.Url, endpoint.EventTypes, endpoint.IsEnabled } },
        });
        await db.SaveChangesAsync(ct);
        return ToResponse(endpoint);
    }

    public async Task<Result<WebhookSecretResponse>> RollSecretAsync(Guid merchantId, Guid endpointId, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(merchantId, endpointId, ct);
        if (endpoint is null)
        {
            return EndpointNotFound();
        }

        var secret = WebhookSecrets.Generate();
        endpoint.RollSecret(protector.Protect(secret, WebhookSecrets.Purpose), secret[^4..], options.Value.SecretRotationOverlap, clock.GetUtcNow());
        audit.Record(new AuditEntry("merchant.webhook_endpoint.secret_rolled", Target, PublicId.Create(IdPrefixes.WebhookEndpoint, endpoint.Id))
        {
            MerchantId = merchantId,
            Changes = new { previousSecretExpiresAt = endpoint.PreviousSecretExpiresAt },
        });
        await db.SaveChangesAsync(ct);
        return new WebhookSecretResponse(ToResponse(endpoint), secret, Notice);
    }

    /// <summary>Sends a <c>webhook.ping</c> event to one endpoint, so merchants can test their receiver without a payment.</summary>
    public async Task<Result<DeliveryResponse>> PingAsync(Guid merchantId, Guid endpointId, CancellationToken ct)
    {
        var endpoint = await FindEndpointAsync(merchantId, endpointId, ct);
        if (endpoint is null)
        {
            return EndpointNotFound();
        }

        if (!endpoint.IsEnabled)
        {
            return Error.Unprocessable("webhook_endpoint_disabled", "Enable the endpoint before sending a test event.");
        }

        var now = clock.GetUtcNow();
        var data = new JsonObject
        {
            ["endpointId"] = PublicId.Create(IdPrefixes.WebhookEndpoint, endpoint.Id),
            ["merchantId"] = PublicId.Create(IdPrefixes.Merchant, merchantId),
            ["message"] = "Test event from PayFlow.",
        };
        var webhookEvent = events.Create(merchantId, WebhookEventTypes.Ping, data, now, sourceMessageId: null);
        var delivery = WebhookDelivery.Schedule(ids.NewId(), webhookEvent.Id, endpoint.Id, merchantId, now);
        db.Set<WebhookEvent>().Add(webhookEvent);
        db.Set<WebhookDelivery>().Add(delivery);
        await db.SaveChangesAsync(ct);
        return ToResponse(delivery, webhookEvent, endpoint, null);
    }

    public async Task<DeliveryPage> ListDeliveriesAsync(Guid merchantId, DeliveryStatus? status, string? eventType, Guid? endpointId, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = from d in db.Set<WebhookDelivery>().AsNoTracking()
                    join ev in db.Set<WebhookEvent>().AsNoTracking() on d.EventId equals ev.Id
                    join en in db.Set<WebhookEndpoint>().AsNoTracking() on d.EndpointId equals en.Id
                    where d.MerchantId == merchantId
                    select new { d, ev, en };
        if (status is { } s)
        {
            query = query.Where(x => x.d.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(eventType))
        {
            query = query.Where(x => x.ev.Type == eventType);
        }

        if (endpointId is { } e)
        {
            query = query.Where(x => x.d.EndpointId == e);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.d.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new DeliveryPage(rows.Select(x => ToResponse(x.d, x.ev, x.en, null)).ToList(), page, pageSize, total);
    }

    public async Task<Result<DeliveryResponse>> GetDeliveryAsync(Guid merchantId, Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.Set<WebhookDelivery>().AsNoTracking().SingleOrDefaultAsync(d => d.Id == deliveryId && d.MerchantId == merchantId, ct);
        if (delivery is null)
        {
            return DeliveryNotFound();
        }

        var ev = await db.Set<WebhookEvent>().AsNoTracking().SingleAsync(e => e.Id == delivery.EventId, ct);
        var en = await db.Set<WebhookEndpoint>().AsNoTracking().SingleAsync(e => e.Id == delivery.EndpointId, ct);
        var attempts = await db.Set<DeliveryAttempt>().AsNoTracking().Where(a => a.DeliveryId == deliveryId).OrderBy(a => a.AttemptNumber).ToListAsync(ct);
        return ToResponse(delivery, ev, en, attempts);
    }

    public async Task<Result<DeliveryResponse>> RedeliverAsync(Guid merchantId, Guid deliveryId, CancellationToken ct)
    {
        var delivery = await db.Set<WebhookDelivery>().SingleOrDefaultAsync(d => d.Id == deliveryId && d.MerchantId == merchantId, ct);
        if (delivery is null)
        {
            return DeliveryNotFound();
        }

        var result = delivery.Redeliver(clock.GetUtcNow());
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry("merchant.webhook_delivery.redelivered", "webhook_delivery", delivery.Id.ToString("N")) { MerchantId = merchantId });
        await db.SaveChangesAsync(ct);
        return await GetDeliveryAsync(merchantId, deliveryId, ct);
    }

    public async Task<Result<EventResponse>> GetEventAsync(Guid merchantId, Guid eventId, CancellationToken ct)
    {
        var ev = await db.Set<WebhookEvent>().AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId && e.MerchantId == merchantId, ct);
        return ev is null ? Error.NotFound("event_not_found", "Event not found.") : ToResponse(ev);
    }

    public async Task<Result<EventPage>> ListEventsAsync(Guid merchantId, string? type, int limit, string? startingAfter, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = db.Set<WebhookEvent>().AsNoTracking().Where(e => e.MerchantId == merchantId);
        if (!string.IsNullOrWhiteSpace(type))
        {
            query = query.Where(e => e.Type == type);
        }

        if (startingAfter is not null)
        {
            if (!PublicId.TryParse(startingAfter, IdPrefixes.Event, out var cursor))
            {
                return Error.Validation("invalid_cursor", "startingAfter must be an evt_... id.");
            }

            query = query.Where(e => e.Id.CompareTo(cursor.Value) < 0);
        }

        var rows = await query.OrderByDescending(e => e.Id).Take(limit + 1).ToListAsync(ct);
        var items = rows.Take(limit).Select(ToResponse).ToList();
        return new EventPage(items, rows.Count > limit, rows.Count > limit ? items[^1].Id : null);
    }

    private Task<WebhookEndpoint?> FindEndpointAsync(Guid merchantId, Guid endpointId, CancellationToken ct) =>
        db.Set<WebhookEndpoint>().SingleOrDefaultAsync(e => e.Id == endpointId && e.MerchantId == merchantId, ct);

    private static Result<IReadOnlyList<string>> ValidateEventTypes(IReadOnlyList<string>? types)
    {
        if (types is null || types.Count == 0)
        {
            return Error.Validation("event_types_required", "Subscribe to at least one event type (or \"*\" for all).");
        }

        var unknown = types.Where(t => !WebhookEventTypes.Subscribable.Contains(t)).ToList();
        return unknown.Count == 0
            ? Result<IReadOnlyList<string>>.Success(types)
            : Error.Validation("unknown_event_type", $"Unknown event type(s): {string.Join(", ", unknown)}. Allowed: {string.Join(", ", WebhookEventTypes.Subscribable.Order(StringComparer.Ordinal))}.");
    }

    private static WebhookEndpointResponse ToResponse(WebhookEndpoint e) => new(
        PublicId.Create(IdPrefixes.WebhookEndpoint, e.Id), e.Url, e.Description, e.EventTypes, e.IsEnabled, e.SecretHint, e.PreviousSecretExpiresAt, e.CreatedAt, e.UpdatedAt);

    private static DeliveryResponse ToResponse(WebhookDelivery d, WebhookEvent ev, WebhookEndpoint en, IReadOnlyList<DeliveryAttempt>? attempts) => new(
        d.Id.ToString("N"),
        PublicId.Create(IdPrefixes.Event, ev.Id),
        ev.Type,
        PublicId.Create(IdPrefixes.WebhookEndpoint, en.Id),
        en.Url,
        d.Status,
        d.AttemptCount,
        d.Status is DeliveryStatus.Pending or DeliveryStatus.Retrying ? d.NextAttemptAt : null,
        d.LastAttemptAt,
        d.SucceededAt,
        d.CreatedAt,
        attempts?.Select(a => new DeliveryAttemptResponse(
                a.AttemptNumber, a.StartedAt, a.DurationMs, a.ResponseStatus, a.ResponseBodyExcerpt, a.ErrorKind, a.ErrorMessage,
                JsonDocument.Parse(a.RequestHeaders).RootElement.Clone()))
            .ToList());

    private static EventResponse ToResponse(WebhookEvent e)
    {
        using var doc = JsonDocument.Parse(e.Payload);
        return new EventResponse(PublicId.Create(IdPrefixes.Event, e.Id), "event", e.Type, e.CreatedAt, doc.RootElement.GetProperty("data").Clone());
    }

    private static Error EndpointNotFound() => Error.NotFound("webhook_endpoint_not_found", "Webhook endpoint not found.");

    private static Error DeliveryNotFound() => Error.NotFound("webhook_delivery_not_found", "Webhook delivery not found.");
}
