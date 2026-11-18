using FluentValidation;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Workers;
using PayFlow.Modules.Payments.Application;
using PayFlow.Modules.Payments.Endpoints;
using PayFlow.Modules.Payments.Infrastructure;

namespace PayFlow.Modules.Payments;

public static class PaymentsModule
{
    public const string Schema = PaymentsDbModel.Schema;

    public static IModuleDbModel DbModel { get; } = new PaymentsDbModel();

    public static IServiceCollection AddPaymentsModule(this IServiceCollection services)
    {
        services.AddSingleton<IModuleDbModel, PaymentsDbModel>();
        services.AddScoped<PaymentService>();
        services.AddScoped<PaymentProcessor>();
        services.AddScoped<PaymentQueries>();
        services.AddScoped<PayFlow.Modules.Payments.Contracts.IPaymentRefundOperations, PaymentRefundOperations>();
        services.AddScoped<PaymentSettlementFeed>();
        services.AddScoped<PayFlow.Modules.Payments.Contracts.IPaymentSettlementFeed>(sp => sp.GetRequiredService<PaymentSettlementFeed>());
        services.AddScoped<PayFlow.Modules.Payments.Contracts.IPaymentLookup>(sp => sp.GetRequiredService<PaymentSettlementFeed>());
        services.AddValidatorsFromAssemblyContaining<CreatePaymentRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);
        services.AddRecurringJob<PendingPaymentsJob>(o => o.PendingPaymentsInterval);
        services.AddRecurringJob<ProcessingRecoveryJob>(o => o.ProcessingRecoveryInterval);
        return services;
    }

    public static IEndpointRouteBuilder MapPaymentsDashboard(this IEndpointRouteBuilder app)
    {
        PaymentEndpoints.MapDashboard(app);
        return app;
    }

    public static IEndpointRouteBuilder MapPaymentsApi(this IEndpointRouteBuilder v1)
    {
        PaymentEndpoints.MapMerchantApi(v1);
        return v1;
    }
}
