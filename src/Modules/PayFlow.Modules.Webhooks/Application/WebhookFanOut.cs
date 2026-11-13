using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.Modules.Webhooks.Domain;
using PayFlow.SharedKernel.Ids;

namespace PayFlow.Modules.Webhooks.Application;

/// <summary>
/// Turns a committed domain event (outbox message) into exactly one webhook event and one delivery
/// per enabled, subscribed endpoint (WH-3, WH-6). Runs inside the outbox dispatcher's transaction.
/// </summary>
internal sealed class WebhookFanOutHandler(PayFlowDbContext db, WebhookEventFactory events, TimeProvider clock) : IOutboxHandler
{
    public string Name => "webhooks.fan-out";

    public bool Handles(string messageType) => WebhookEventTypes.Deliverable.Contains(messageType);

    public async Task HandleAsync(OutboxMessage message, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        var data = JsonNode.Parse(message.Payload) ?? throw new InvalidOperationException("Outbox payload is empty.");
        var merchantPublicId = data["merchantId"]?.GetValue<string>();
        if (!PublicId.TryParse(merchantPublicId, IdPrefixes.Merchant, out var merchantId))
        {
            throw new InvalidOperationException($"Outbox message {message.Id} of type {message.Type} has no merchantId.");
        }

        var webhookEvent = events.Create(merchantId.Value, message.Type, data, message.OccurredAt, message.Id);
        db.Set<WebhookEvent>().Add(webhookEvent);

        var endpoints = await db.Set<WebhookEndpoint>().AsNoTracking()
            .Where(e => e.MerchantId == merchantId && e.IsEnabled)
            .ToListAsync(cancellationToken);
        var now = clock.GetUtcNow();
        foreach (var endpoint in endpoints.Where(e => e.Subscribes(message.Type)))
        {
            db.Set<WebhookDelivery>().Add(WebhookDelivery.Schedule(events.NewId(), webhookEvent.Id, endpoint.Id, merchantId.Value, now));
        }
    }
}

/// <summary>Builds the event envelope: the exact JSON body every endpoint receives for this event.</summary>
internal sealed class WebhookEventFactory(IdGenerator ids, TimeProvider clock, IOptions<WebhookOptions> options)
{
    public Guid NewId() => ids.NewId();

    public WebhookEvent Create(Guid merchantId, string type, JsonNode data, DateTimeOffset occurredAt, Guid? sourceMessageId)
    {
        ArgumentNullException.ThrowIfNull(data);
        var id = ids.NewId();
        var envelope = new JsonObject
        {
            ["id"] = PublicId.Create(IdPrefixes.Event, id),
            ["object"] = "event",
            ["type"] = type,
            ["apiVersion"] = options.Value.ApiVersion,
            ["createdAt"] = occurredAt,
            ["livemode"] = false,
            ["simulated"] = true,
            ["data"] = data.DeepClone(),
        };
        return new WebhookEvent
        {
            Id = id,
            MerchantId = merchantId,
            Type = type,
            ApiVersion = options.Value.ApiVersion,
            Payload = envelope.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            OccurredAt = occurredAt,
            CreatedAt = clock.GetUtcNow(),
            SourceMessageId = sourceMessageId,
        };
    }
}
