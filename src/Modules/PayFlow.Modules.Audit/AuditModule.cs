using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Application;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Audit.Infrastructure;

namespace PayFlow.Modules.Audit;

public static class AuditModule
{
    public const string Schema = AuditDbModel.Schema;
    public const string EventsTable = AuditDbModel.EventsTable;

    public static IServiceCollection AddAuditModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleDbModel, AuditDbModel>();
        services.AddScoped<IAuditLog, EfAuditLog>();
        services.AddScoped<IAuditQuery, EfAuditQuery>();
        return services;
    }

    /// <summary>Exposes the module's EF mapping for the design-time migrations factory.</summary>
    public static IModuleDbModel DbModel { get; } = new AuditDbModel();
}
