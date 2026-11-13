using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Webhooks.Application;
using PayFlow.Modules.Webhooks.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Webhooks.Endpoints;

internal sealed class CreateWebhookEndpointRequestValidator : AbstractValidator<CreateWebhookEndpointRequest>
{
    public CreateWebhookEndpointRequestValidator()
    {
        RuleFor(r => r.Url).NotEmpty().MaximumLength(2048);
        RuleFor(r => r.Description).MaximumLength(200);
        RuleFor(r => r.EventTypes).NotEmpty();
        RuleForEach(r => r.EventTypes).NotEmpty().MaximumLength(60);
    }
}

internal sealed class UpdateWebhookEndpointRequestValidator : AbstractValidator<UpdateWebhookEndpointRequest>
{
    public UpdateWebhookEndpointRequestValidator()
    {
        RuleFor(r => r.Url).MaximumLength(2048);
        RuleFor(r => r.Description).MaximumLength(200);
        RuleForEach(r => r.EventTypes).NotEmpty().MaximumLength(60);
    }
}

internal static class WebhookEndpoints
{
    private static readonly Error EndpointNotFound = Error.NotFound("webhook_endpoint_not_found", "Webhook endpoint not found.");
    private static readonly Error DeliveryNotFound = Error.NotFound("webhook_delivery_not_found", "Webhook delivery not found.");

    public static void MapDashboard(IEndpointRouteBuilder app)
    {
        var endpoints = app.MapGroup("/api/merchants/{merchantId}/webhook-endpoints").WithTags("Webhooks").RequireAuthorization(AuthPolicies.MerchantAdmin);

        endpoints.MapGet("/", (string merchantId, WebhookService service, CancellationToken ct) =>
            WithMerchant(merchantId, async m => Results.Ok(await service.ListEndpointsAsync(m, ct))));

        endpoints.MapPost("/", (string merchantId, CreateWebhookEndpointRequest request, WebhookService service, CancellationToken ct) =>
                WithMerchant(merchantId, async m => (await service.CreateEndpointAsync(m, request, ct))
                    .ToHttp(r => Results.Created($"/api/merchants/{merchantId}/webhook-endpoints/{r.Endpoint.Id}", r))))
            .WithValidation<CreateWebhookEndpointRequest>();

        endpoints.MapPatch("/{endpointId}", (string merchantId, string endpointId, UpdateWebhookEndpointRequest request, WebhookService service, CancellationToken ct) =>
                WithIds(merchantId, endpointId, IdPrefixes.WebhookEndpoint, EndpointNotFound, async (m, e) => (await service.UpdateEndpointAsync(m, e, request, ct)).ToHttp()))
            .WithValidation<UpdateWebhookEndpointRequest>();

        endpoints.MapPost("/{endpointId}/roll-secret", (string merchantId, string endpointId, WebhookService service, CancellationToken ct) =>
            WithIds(merchantId, endpointId, IdPrefixes.WebhookEndpoint, EndpointNotFound, async (m, e) => (await service.RollSecretAsync(m, e, ct)).ToHttp()));

        endpoints.MapPost("/{endpointId}/ping", (string merchantId, string endpointId, WebhookService service, CancellationToken ct) =>
            WithIds(merchantId, endpointId, IdPrefixes.WebhookEndpoint, EndpointNotFound, async (m, e) => (await service.PingAsync(m, e, ct)).ToHttp(d => Results.Accepted(value: d))));

        var deliveries = app.MapGroup("/api/merchants/{merchantId}/webhook-deliveries").WithTags("Webhooks");

        deliveries.MapGet("/", (string merchantId, DeliveryStatus? status, string? eventType, string? endpointId, int? page, int? pageSize, WebhookService service, CancellationToken ct) =>
                WithMerchant(merchantId, async m =>
                {
                    Guid? endpoint = null;
                    if (endpointId is not null)
                    {
                        if (!PublicId.TryParse(endpointId, IdPrefixes.WebhookEndpoint, out var parsed))
                        {
                            return ErrorResults.Problem(EndpointNotFound);
                        }

                        endpoint = parsed;
                    }

                    return Results.Ok(await service.ListDeliveriesAsync(m, status, eventType, endpoint, page ?? 1, pageSize ?? 25, ct));
                }))
            .RequireAuthorization(AuthPolicies.MerchantMember);

        deliveries.MapGet("/{deliveryId}", (string merchantId, string deliveryId, WebhookService service, CancellationToken ct) =>
                WithDelivery(merchantId, deliveryId, async (m, d) => (await service.GetDeliveryAsync(m, d, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.MerchantMember);

        deliveries.MapPost("/{deliveryId}/redeliver", (string merchantId, string deliveryId, WebhookService service, CancellationToken ct) =>
                WithDelivery(merchantId, deliveryId, async (m, d) => (await service.RedeliverAsync(m, d, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.MerchantAdmin);
    }

    public static void MapMerchantApi(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/events", async (string? type, int? limit, string? startingAfter, ICurrentMerchant merchant, WebhookService service, CancellationToken ct) =>
                (await service.ListEventsAsync(merchant.MerchantId!.Value, type, limit ?? 25, startingAfter, ct)).ToHttp())
            .WithTags("Merchant API: events")
            .RequireAuthorization(MerchantApiAuth.Policy);

        v1.MapGet("/events/{eventId}", async (string eventId, ICurrentMerchant merchant, WebhookService service, CancellationToken ct) =>
                PublicId.TryParse(eventId, IdPrefixes.Event, out var id)
                    ? (await service.GetEventAsync(merchant.MerchantId!.Value, id.Value, ct)).ToHttp()
                    : ErrorResults.Problem(Error.NotFound("event_not_found", "Event not found.")))
            .WithTags("Merchant API: events")
            .RequireAuthorization(MerchantApiAuth.Policy);
    }

    private static async Task<IResult> WithMerchant(string merchantId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var id)
            ? await handler(id.Value)
            : ErrorResults.Problem(Error.NotFound("merchant_not_found", "Merchant not found."));

    private static async Task<IResult> WithIds(string merchantId, string resourceId, string prefix, Error notFound, Func<Guid, Guid, Task<IResult>> handler) =>
        await WithMerchant(merchantId, async m => PublicId.TryParse(resourceId, prefix, out var r) ? await handler(m, r.Value) : ErrorResults.Problem(notFound));

    /// <summary>Delivery ids are plain 32-hex UUIDs (internal records, not a public API object).</summary>
    private static async Task<IResult> WithDelivery(string merchantId, string deliveryId, Func<Guid, Guid, Task<IResult>> handler) =>
        await WithMerchant(merchantId, async m => Guid.TryParseExact(deliveryId, "N", out var d) ? await handler(m, d) : ErrorResults.Problem(DeliveryNotFound));
}
