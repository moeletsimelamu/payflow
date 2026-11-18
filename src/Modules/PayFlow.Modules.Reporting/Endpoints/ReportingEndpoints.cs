using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Reporting.Application;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Reporting.Endpoints;

internal static class ReportingEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        // Platform view: every merchant, or one; includes the reconciliation backlog.
        app.MapGet("/api/reports", async (string? merchantId, string? currency, DateOnly? from, DateOnly? to, ReportingService service, TimeProvider clock, CancellationToken ct) =>
            {
                Guid? merchant = null;
                if (merchantId is not null)
                {
                    if (!PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var m))
                    {
                        return ErrorResults.Problem(Error.Validation("invalid_merchant_id", "merchantId must be a mer_... id."));
                    }

                    merchant = m;
                }

                return await RunAsync(ReportingService.Validate(merchant, currency, from, to, includeReconciliation: true, clock.GetUtcNow()), service, ct);
            })
            .WithTags("Reporting")
            .RequireAuthorization(AuthPolicies.PlatformAdmin);

        // Merchant view: that merchant only; reconciliation is a platform concern and is left out.
        app.MapGet("/api/merchants/{merchantId}/reports", async (string merchantId, string? currency, DateOnly? from, DateOnly? to, ReportingService service, TimeProvider clock, CancellationToken ct) =>
            {
                if (!PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var m))
                {
                    return ErrorResults.Problem(Error.NotFound("merchant_not_found", "Merchant not found."));
                }

                return await RunAsync(ReportingService.Validate(m.Value, currency, from, to, includeReconciliation: false, clock.GetUtcNow()), service, ct);
            })
            .WithTags("Reporting")
            .RequireAuthorization(AuthPolicies.MerchantMember);
    }

    private static async Task<IResult> RunAsync(Result<ReportQuery> query, ReportingService service, CancellationToken ct) =>
        query.IsSuccess ? Results.Ok(await service.GetAsync(query.Value, ct)) : ErrorResults.Problem(query.Error);
}
