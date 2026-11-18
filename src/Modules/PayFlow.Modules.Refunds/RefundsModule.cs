using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Workers;
using PayFlow.Modules.Refunds.Application;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Endpoints;
using PayFlow.Modules.Refunds.Infrastructure;

namespace PayFlow.Modules.Refunds;

public static class RefundsModule
{
    public const string Schema = RefundsDbModel.Schema;

    public static IModuleDbModel DbModel { get; } = new RefundsDbModel();

    public static IServiceCollection AddRefundsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleDbModel, RefundsDbModel>();
        services.AddScoped<RefundService>();
        services.AddScoped<RefundQueries>();
        services.AddScoped<RefundSettlementFeed>();
        services.AddScoped<IRefundSettlementFeed>(sp => sp.GetRequiredService<RefundSettlementFeed>());
        services.AddScoped<IRefundLookup>(sp => sp.GetRequiredService<RefundSettlementFeed>());
        services.TryAddScoped<ISettlementLookup, NoSettlementsYet>(); // the Settlement module registers the real lookup first
        services.AddValidatorsFromAssemblyContaining<CreateRefundRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);
        services.AddRecurringJob<RefundProcessingJob>(o => o.RefundProcessingInterval);
        return services;
    }

    public static IEndpointRouteBuilder MapRefundsDashboard(this IEndpointRouteBuilder app)
    {
        RefundEndpoints.MapDashboard(app);
        return app;
    }

    public static IEndpointRouteBuilder MapRefundsApi(this IEndpointRouteBuilder v1)
    {
        RefundEndpoints.MapMerchantApi(v1);
        return v1;
    }
}
