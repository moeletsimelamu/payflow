using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Application;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Merchants.Endpoints;

internal static class MerchantEndpoints
{
    public static void MapDashboardEndpoints(IEndpointRouteBuilder app)
    {
        var merchants = app.MapGroup("/api/merchants").WithTags("Merchants");

        merchants.MapPost("/", async (RegisterMerchantRequest request, MerchantService service, CancellationToken ct) =>
                (await service.RegisterAsync(request, ct)).ToHttp(r => Results.Created($"/api/merchants/{r.Merchant.Id}", r)))
            .RequireAuthorization(AuthPolicies.PlatformAdmin)
            .WithValidation<RegisterMerchantRequest>();

        merchants.MapGet("/", async (MerchantStatus? status, string? search, int? page, int? pageSize, MerchantService service, CancellationToken ct) =>
                Results.Ok(await service.ListAsync(status, search, page ?? 1, pageSize ?? 25, ct)))
            .RequireAuthorization(AuthPolicies.PlatformAdmin);

        var merchant = merchants.MapGroup("/{merchantId}");

        merchant.MapGet("/", (string merchantId, MerchantService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.GetAsync(id, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.MerchantMember);

        merchant.MapPatch("/", (string merchantId, UpdateMerchantProfileRequest request, MerchantService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.UpdateProfileAsync(id, request, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.MerchantAdmin)
            .WithValidation<UpdateMerchantProfileRequest>();

        merchant.MapPost("/status", (string merchantId, ChangeMerchantStatusRequest request, MerchantService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.ChangeStatusAsync(id, request, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.PlatformAdmin)
            .WithValidation<ChangeMerchantStatusRequest>();

        merchant.MapPut("/currencies/{currency}", (string merchantId, string currency, CurrencyLimitsRequest request, MerchantService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.ConfigureCurrencyAsync(id, currency.ToUpperInvariant(), request, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.PlatformAdmin)
            .WithValidation<CurrencyLimitsRequest>();

        merchant.MapPut("/fee-plan", (string merchantId, ChangeFeePlanRequest request, MerchantService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.ChangeFeePlanAsync(id, request, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.PlatformAdmin)
            .WithValidation<ChangeFeePlanRequest>();

        merchant.MapGet("/activity", (string merchantId, long? before, int? limit, MemberService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.ActivityAsync(id, before, limit ?? 50, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.MerchantMember);

        var feePlans = app.MapGroup("/api/fee-plans").WithTags("Fee plans").RequireAuthorization(AuthPolicies.PlatformAdmin);
        feePlans.MapGet("/", async (MerchantService service, CancellationToken ct) => Results.Ok(await service.ListFeePlansAsync(ct)));
        feePlans.MapPost("/", async (CreateFeePlanRequest request, MerchantService service, CancellationToken ct) =>
                (await service.CreateFeePlanAsync(request, ct)).ToHttp(p => Results.Created($"/api/fee-plans/{p.Id}", p)))
            .WithValidation<CreateFeePlanRequest>();
    }

    public static void MapMerchantApiEndpoints(IEndpointRouteBuilder v1)
    {
        v1.MapGet("/merchant", async (ICurrentMerchant current, MerchantService service, CancellationToken ct) =>
                (await service.GetApiProfileAsync(current.MerchantId!.Value, ct)).ToHttp())
            .WithTags("Merchant API")
            .RequireAuthorization(MerchantApiAuth.Policy);
    }

    /// <summary>Parses the <c>{merchantId}</c> route value. Malformed ids are reported as not found.</summary>
    internal static async Task<IResult> WithMerchant(string merchantId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var id)
            ? await handler(id.Value)
            : ErrorResults.Problem(Mapping.MerchantNotFound());

    internal static async Task<IResult> WithIds(string merchantId, string resourceId, string prefix, Error notFound, Func<Guid, Guid, Task<IResult>> handler)
    {
        if (!PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var merchant))
        {
            return ErrorResults.Problem(Mapping.MerchantNotFound());
        }

        return PublicId.TryParse(resourceId, prefix, out var resource)
            ? await handler(merchant.Value, resource.Value)
            : ErrorResults.Problem(notFound);
    }
}
