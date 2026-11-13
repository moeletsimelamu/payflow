using System.Text;
using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Payments.Application;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Payments.Endpoints;

internal sealed class CreatePaymentRequestValidator : AbstractValidator<CreatePaymentRequest>
{
    public CreatePaymentRequestValidator()
    {
        RuleFor(r => r.Amount).NotEmpty().MaximumLength(32);
        RuleFor(r => r.Currency).NotEmpty().Length(3);
        RuleFor(r => r.PaymentMethod).NotEmpty().Must(PaymentSimulator.IsSupported)
            .WithMessage($"paymentMethod must be a simulation token: {string.Join(", ", PaymentSimulator.SupportedMethods)}.");
        RuleFor(r => r.MerchantReference).NotEmpty().MaximumLength(100);
        RuleFor(r => r.Description).MaximumLength(500);
        RuleFor(r => r.Metadata).Must(m => m is null || m.Count <= 20).WithMessage("metadata allows at most 20 keys.");
        RuleForEach(r => r.Metadata).ChildRules(kv =>
        {
            kv.RuleFor(x => x.Key).NotEmpty().MaximumLength(40);
            kv.RuleFor(x => x.Value).MaximumLength(500);
        });
    }
}

internal static class PaymentEndpoints
{
    private static readonly Error PaymentNotFound = Error.NotFound("payment_not_found", "Payment not found.");

    public static void MapMerchantApi(IEndpointRouteBuilder v1)
    {
        var payments = v1.MapGroup("/payments").WithTags("Merchant API: payments").RequireAuthorization(MerchantApiAuth.Policy);

        payments.MapPost("/", async (
                CreatePaymentRequest request,
                [FromHeader(Name = IdempotencyKeys.HeaderName)] string? idempotencyKey,
                ICurrentMerchant merchant,
                PaymentService service,
                HttpContext http,
                CancellationToken ct) =>
            {
                var result = await service.CreateAsync(merchant.MerchantId!.Value, idempotencyKey, request, ct);
                if (!result.IsSuccess)
                {
                    if (result.Error.Code == "idempotency_request_in_progress")
                    {
                        http.Response.Headers.RetryAfter = "1";
                    }

                    return ErrorResults.Problem(result.Error);
                }

                var creation = result.Value;
                if (creation.Replayed)
                {
                    http.Response.Headers["Idempotent-Replayed"] = "true";
                }

                if (creation.PaymentId is not null)
                {
                    http.Response.Headers.Location = $"/v1/payments/{creation.PaymentId}";
                }

                return Results.Text(creation.Body, "application/json", Encoding.UTF8, creation.StatusCode);
            })
            .WithValidation<CreatePaymentRequest>();

        payments.MapGet("/{paymentId}", (string paymentId, ICurrentMerchant merchant, PaymentQueries queries, CancellationToken ct) =>
            WithPayment(paymentId, async id => (await queries.GetAsync(merchant.MerchantId!.Value, id, ct)).ToHttp()));

        payments.MapGet("/", async (PaymentStatus? status, int? limit, string? startingAfter, ICurrentMerchant merchant, PaymentQueries queries, CancellationToken ct) =>
            (await queries.ListAsync(merchant.MerchantId!.Value, status, limit ?? 25, startingAfter, ct)).ToHttp());
    }

    public static void MapDashboard(IEndpointRouteBuilder app)
    {
        app.MapGet("/api/merchants/{merchantId}/payments", (
                string merchantId, PaymentStatus? status, string? failureCode, string? reference, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
                PaymentQueries queries, CancellationToken ct) =>
                WithMerchant(merchantId, async m => Results.Ok(await queries.SearchAsync(m, status, failureCode, reference, from, to, page ?? 1, pageSize ?? 25, ct))))
            .WithTags("Payments")
            .RequireAuthorization(AuthPolicies.MerchantMember);

        app.MapGet("/api/merchants/{merchantId}/payments/{paymentId}", (string merchantId, string paymentId, PaymentQueries queries, CancellationToken ct) =>
                WithMerchant(merchantId, m => WithPayment(paymentId, async p => (await queries.GetDetailAsync(m, p, ct)).ToHttp())))
            .WithTags("Payments")
            .RequireAuthorization(AuthPolicies.MerchantMember);

        app.MapGet("/api/payments", async (
                string? merchantId, PaymentStatus? status, string? failureCode, string? reference, DateTimeOffset? from, DateTimeOffset? to, int? page, int? pageSize,
                PaymentQueries queries, CancellationToken ct) =>
            {
                Guid? merchant = null;
                if (merchantId is not null)
                {
                    if (!PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var parsed))
                    {
                        return ErrorResults.Problem(Error.Validation("invalid_merchant_id", "merchantId must be a mer_... id."));
                    }

                    merchant = parsed;
                }

                return Results.Ok(await queries.SearchAsync(merchant, status, failureCode, reference, from, to, page ?? 1, pageSize ?? 25, ct));
            })
            .WithTags("Payments")
            .RequireAuthorization(AuthPolicies.PlatformAdmin);
    }

    private static async Task<IResult> WithPayment(string paymentId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(paymentId, IdPrefixes.Payment, out var id) ? await handler(id.Value) : ErrorResults.Problem(PaymentNotFound);

    private static async Task<IResult> WithMerchant(string merchantId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var id)
            ? await handler(id.Value)
            : ErrorResults.Problem(Error.NotFound("merchant_not_found", "Merchant not found."));
}
