using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Workers;
using PayFlow.Modules.Webhooks.Application;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.Modules.Webhooks.Endpoints;
using PayFlow.Modules.Webhooks.Infrastructure;

namespace PayFlow.Modules.Webhooks;

public static class WebhooksModule
{
    public const string Schema = WebhooksDbModel.Schema;
    public const string HttpClientName = WebhookDeliveryJob.HttpClientName;

    public static IModuleDbModel DbModel { get; } = new WebhooksDbModel();

    public static IServiceCollection AddWebhooksModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IModuleDbModel, WebhooksDbModel>();
        services.Configure<WebhookOptions>(configuration.GetSection(WebhookOptions.Section));
        services.AddScoped<WebhookEventFactory>();
        services.AddScoped<WebhookService>();
        services.AddScoped<IOutboxHandler, WebhookFanOutHandler>();
        services.AddValidatorsFromAssemblyContaining<CreateWebhookEndpointRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);

        // Webhook HTTP calls go through the SSRF-guarded handler only.
        services.AddHttpClient(HttpClientName, client => client.Timeout = Timeout.InfiniteTimeSpan) // per-request timeout in the job
            .ConfigurePrimaryHttpMessageHandler(sp => SsrfGuard.CreateHandler(sp.GetRequiredService<IOptions<WebhookOptions>>().Value));

        services.AddRecurringJob<WebhookDeliveryJob>(o => o.WebhookDeliveryInterval);
        return services;
    }

    public static IEndpointRouteBuilder MapWebhooksDashboard(this IEndpointRouteBuilder app)
    {
        WebhookEndpoints.MapDashboard(app);
        return app;
    }

    public static IEndpointRouteBuilder MapWebhooksApi(this IEndpointRouteBuilder v1)
    {
        WebhookEndpoints.MapMerchantApi(v1);
        return v1;
    }
}
