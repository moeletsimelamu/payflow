using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Reconciliation.Application;
using PayFlow.Modules.Reconciliation.Endpoints;
using PayFlow.Modules.Reconciliation.Infrastructure;

namespace PayFlow.Modules.Reconciliation;

public static class ReconciliationModule
{
    public const string Schema = ReconciliationDbModel.Schema;

    public static IModuleDbModel DbModel { get; } = new ReconciliationDbModel();

    public static IServiceCollection AddReconciliationModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleDbModel, ReconciliationDbModel>();
        services.AddScoped<ReconciliationService>();
        services.AddScoped<StatementGenerator>();
        services.AddValidatorsFromAssemblyContaining<ImportStatementRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);
        return services;
    }

    public static IEndpointRouteBuilder MapReconciliationEndpoints(this IEndpointRouteBuilder app)
    {
        ReconciliationEndpoints.Map(app);
        return app;
    }
}
