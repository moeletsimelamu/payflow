using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Modules.Reporting.Application;
using PayFlow.Modules.Reporting.Endpoints;

namespace PayFlow.Modules.Reporting;

/// <summary>Read-only reports over the other modules' schemas (RPT-1..6). Owns no tables.</summary>
public static class ReportingModule
{
    public static IServiceCollection AddReportingModule(this IServiceCollection services)
    {
        services.AddScoped<ReportingService>();
        return services;
    }

    public static IEndpointRouteBuilder MapReportingEndpoints(this IEndpointRouteBuilder app)
    {
        ReportingEndpoints.Map(app);
        return app;
    }
}
