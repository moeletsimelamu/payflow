using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger.Application;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Ledger.Infrastructure;

namespace PayFlow.Modules.Ledger;

public static class LedgerModule
{
    public const string Schema = LedgerDbModel.Schema;
    public const string BalanceTriggersSql = LedgerDbModel.BalanceTriggersSql;
    public const string DropBalanceTriggersSql = LedgerDbModel.DropBalanceTriggersSql;

    public static IModuleDbModel DbModel { get; } = new LedgerDbModel();

    public static IServiceCollection AddLedgerModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleDbModel, LedgerDbModel>();
        services.AddScoped<ILedger, EfLedger>();
        return services;
    }
}
