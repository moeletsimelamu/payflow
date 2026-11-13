using FluentValidation;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Merchants.Application;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Endpoints;
using PayFlow.Modules.Merchants.Infrastructure;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Merchants;

public static class MerchantsModule
{
    public const string Schema = MerchantsDbModel.Schema;

    public static IModuleDbModel DbModel { get; } = new MerchantsDbModel();

    public static IServiceCollection AddMerchantsModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IModuleDbModel, MerchantsDbModel>();
        services.Configure<ApiKeySecurityOptions>(configuration.GetSection(ApiKeySecurityOptions.Section));

        services.AddSingleton<ApiKeyCredentials>();
        services.AddSingleton<ApiKeyUsageTracker>();
        services.AddSingleton<ApiKeyCache>();
        services.AddScoped<ApiKeyAuthenticator>();
        services.AddScoped<ICurrentMerchant, CurrentMerchant>();
        services.AddScoped<IMerchantDirectory, MerchantDirectory>();
        services.AddScoped<MerchantService>();
        services.AddScoped<ApiKeyService>();
        services.AddScoped<MemberService>();
        services.AddValidatorsFromAssemblyContaining<RegisterMerchantRequestValidator>(ServiceLifetime.Scoped, includeInternalTypes: true);

        services.AddAuthentication()
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(MerchantApiAuth.Scheme, _ => { });
        services.AddAuthorizationBuilder()
            .AddPolicy(MerchantApiAuth.Policy, p => p
                .AddAuthenticationSchemes(MerchantApiAuth.Scheme)
                .RequireAuthenticatedUser()
                .RequireClaim(PayFlowClaimTypes.MerchantId));

        return services;
    }

    /// <summary>Dashboard endpoints under <c>/api</c>.</summary>
    public static IEndpointRouteBuilder MapMerchantsDashboard(this IEndpointRouteBuilder app)
    {
        MerchantEndpoints.MapDashboardEndpoints(app);
        ApiKeyAndMemberEndpoints.Map(app);
        return app;
    }

    /// <summary>Merchant API endpoints, mapped onto the host's <c>/v1</c> group.</summary>
    public static IEndpointRouteBuilder MapMerchantsApi(this IEndpointRouteBuilder v1)
    {
        MerchantEndpoints.MapMerchantApiEndpoints(v1);
        return v1;
    }
}
