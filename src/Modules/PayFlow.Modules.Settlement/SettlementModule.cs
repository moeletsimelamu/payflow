using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Settlement.Application;
using PayFlow.Modules.Settlement.Endpoints;
using PayFlow.Modules.Settlement.Infrastructure;

namespace PayFlow.Modules.Settlement;

public static class SettlementModule
{
    public const string Schema = SettlementDbModel.Schema;
    public const string SettlementPaidEventType = SettlementService.SettlementPaidEventType;

    public static IModuleDbModel DbModel { get; } = new SettlementDbModel();

    public static IServiceCollection AddSettlementModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleDbModel, SettlementDbModel>();
        services.AddScoped<SettlementService>();
        services.AddScoped<SettlementComposer>();

        // The real "is this payment settled?" check for reversals replaces the Phase 5 placeholder.
        services.RemoveAll<ISettlementLookup>();
        services.AddScoped<ISettlementLookup, SettlementLookup>();

        services.AddValidatorsFromAssemblyContaining<SettlementRunRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapSettlementEndpoints(this IEndpointRouteBuilder app)
    {
        SettlementEndpoints.Map(app);
        return app;
    }
}
