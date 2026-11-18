using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Settlement.Application;
using PayFlow.Modules.Settlement.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Settlement.Endpoints;

internal sealed class SettlementRunRequestValidator : AbstractValidator<SettlementRunRequest>
{
    public SettlementRunRequestValidator()
    {
        RuleFor(r => r.MerchantId).MaximumLength(40);
        RuleFor(r => r.Currency).Length(3).When(r => r.Currency is not null);
    }
}

internal sealed class PaySettlementRequestValidator : AbstractValidator<PaySettlementRequest>;

internal static class SettlementEndpoints
{
    private static readonly Error NotFound = Error.NotFound("settlement_not_found", "Settlement batch not found.");

    public static void Map(IEndpointRouteBuilder app)
    {
        var platform = app.MapGroup("/api/settlements").WithTags("Settlement").RequireAuthorization(AuthPolicies.PlatformAdmin);

        platform.MapPost("/runs", async (SettlementRunRequest request, SettlementService service, CancellationToken ct) =>
                (await service.RunAsync(request, ct)).ToHttp())
            .WithValidation<SettlementRunRequest>();

        platform.MapGet("/", async (string? merchantId, string? currency, SettlementStatus? status, int? page, int? pageSize, SettlementService service, CancellationToken ct) =>
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

            return Results.Ok(await service.ListAsync(merchant, currency, status, page ?? 1, pageSize ?? 25, ct));
        });

        platform.MapGet("/{settlementId}", (string settlementId, SettlementService service, CancellationToken ct) =>
            WithBatch(settlementId, async b => (await service.GetAsync(b, null, ct)).ToHttp()));

        platform.MapPost("/{settlementId}/pay", (string settlementId, PaySettlementRequest request, SettlementService service, CancellationToken ct) =>
                WithBatch(settlementId, async b => (await service.PayAsync(b, request, ct)).ToHttp()))
            .WithValidation<PaySettlementRequest>();

        var merchant = app.MapGroup("/api/merchants/{merchantId}/settlements").WithTags("Settlement").RequireAuthorization(AuthPolicies.MerchantMember);

        merchant.MapGet("/", (string merchantId, string? currency, SettlementStatus? status, int? page, int? pageSize, SettlementService service, CancellationToken ct) =>
            WithMerchant(merchantId, async m => Results.Ok(await service.ListAsync(m, currency, status, page ?? 1, pageSize ?? 25, ct))));

        merchant.MapGet("/{settlementId}", (string merchantId, string settlementId, SettlementService service, CancellationToken ct) =>
            WithMerchant(merchantId, m => WithBatch(settlementId, async b => (await service.GetAsync(b, m, ct)).ToHttp())));

        merchant.MapGet("/{settlementId}/items.csv", (string merchantId, string settlementId, SettlementService service, CancellationToken ct) =>
            WithMerchant(merchantId, m => WithBatch(settlementId, async b => (await service.ExportCsvAsync(b, m, ct))
                .ToHttp(csv => Results.File(Encoding.UTF8.GetBytes(csv), "text/csv", $"{settlementId}.csv")))));
    }

    private static async Task<IResult> WithBatch(string settlementId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(settlementId, IdPrefixes.Settlement, out var id) ? await handler(id.Value) : ErrorResults.Problem(NotFound);

    private static async Task<IResult> WithMerchant(string merchantId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var id)
            ? await handler(id.Value)
            : ErrorResults.Problem(Error.NotFound("merchant_not_found", "Merchant not found."));
}
