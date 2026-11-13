using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using PayFlow.Infrastructure;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit;
using PayFlow.Modules.Identity;
using PayFlow.Modules.Ledger;
using PayFlow.Modules.Merchants;
using PayFlow.Modules.Payments;
using PayFlow.Modules.Refunds;
using PayFlow.Modules.Webhooks;

namespace PayFlow.Migrations;

/// <summary>
/// Used only by <c>dotnet ef</c> to build the model when adding migrations. Every module's EF
/// mapping must be listed here (and the architecture tests check that).
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PayFlowDbContext>
{
    public static IReadOnlyList<IModuleDbModel> Modules { get; } =
    [
        IdentityModule.DbModel,
        AuditModule.DbModel,
        MerchantsModule.DbModel,
        LedgerModule.DbModel,
        PaymentsModule.DbModel,
        RefundsModule.DbModel,
        WebhooksModule.DbModel,
    ];

    public PayFlowDbContext CreateDbContext(string[] args)
    {
        // No real database is contacted when generating migrations.
        var options = new DbContextOptionsBuilder<PayFlowDbContext>();
        options.UsePayFlowPostgres("Host=localhost;Database=payflow_design_time");
        return new PayFlowDbContext(options.Options, Modules);
    }
}
