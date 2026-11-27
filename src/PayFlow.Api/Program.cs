using PayFlow.Api.Hosting;
using PayFlow.Api.Logging;
using PayFlow.Infrastructure;
using PayFlow.Modules.Audit;
using PayFlow.Modules.Identity;
using PayFlow.Modules.Ledger;
using PayFlow.Modules.Merchants;
using PayFlow.Modules.Payments;
using PayFlow.Modules.Reconciliation;
using PayFlow.Modules.Refunds;
using PayFlow.Modules.Reporting;
using PayFlow.Modules.Settlement;
using PayFlow.Modules.Webhooks;
using PayFlow.SharedKernel.Security;
using Serilog;

var builder = WebApplication.CreateBuilder(args);

builder.Host.UseSerilog(LoggingSetup.ConfigureSerilog);

builder.Services
    .AddPayFlowInfrastructure(builder.Configuration)
    .AddAuditModule()
    .AddIdentityModule(builder.Configuration)
    .AddMerchantsModule(builder.Configuration)
    .AddLedgerModule()
    .AddPaymentsModule()
    .AddRefundsModule()
    .AddWebhooksModule(builder.Configuration)
    .AddSettlementModule()
    .AddReconciliationModule()
    .AddReportingModule()
    .AddScoped<IActorContext, HttpActorContext>()
    .AddPayFlowApi(builder.Configuration)
    .AddPayFlowTelemetry(builder.Configuration);

var app = builder.Build();

if (args.Contains("--migrate", StringComparer.Ordinal))
{
    await DatabaseStartup.MigrateAsync(app.Services);
    return;
}

await DatabaseStartup.InitialiseAsync(app);

app.UsePayFlowPipeline();
app.MapPayFlowHealthChecks();
app.MapIdentityEndpoints();
app.MapMerchantsDashboard();
app.MapPaymentsDashboard();
app.MapRefundsDashboard();
app.MapWebhooksDashboard();
app.MapSettlementEndpoints();
app.MapReconciliationEndpoints();
app.MapReportingEndpoints();

var merchantApi = app.MapMerchantApiGroup();
merchantApi.MapMerchantsApi();
merchantApi.MapPaymentsApi();
merchantApi.MapRefundsApi();
merchantApi.MapWebhooksApi();

await app.RunAsync();

/// <summary>Entry point; public for <c>WebApplicationFactory</c> integration tests.</summary>
public partial class Program;
