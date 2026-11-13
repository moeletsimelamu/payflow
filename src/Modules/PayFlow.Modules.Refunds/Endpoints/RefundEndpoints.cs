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
using PayFlow.Modules.Refunds.Application;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Refunds.Endpoints;

internal sealed class CreateRefundRequestValidator : AbstractValidator<CreateRefundRequest>
{
    public CreateRefundRequestValidator()
    {
        RuleFor(r => r.Amount).MaximumLength(32);
        RuleFor(r => r.Reason).MaximumLength(300);
    }
}

internal sealed class CreateReversalRequestValidator : AbstractValidator<CreateReversalRequest>
{
    public CreateReversalRequestValidator() => RuleFor(r => r.Reason).MaximumLength(300);
}

internal static class RefundEndpoints
{
    private static readonly Error PaymentNotFound = Error.NotFound("payment_not_found", "Payment not found.");

    public static void MapMerchantApi(IEndpointRouteBuilder v1)
    {
        v1.MapPost("/payments/{paymentId}/refunds", (
                string paymentId, CreateRefundRequest request, [FromHeader(Name = IdempotencyKeys.HeaderName)] string? idempotencyKey,
                ICurrentMerchant merchant, RefundService service, HttpContext http, CancellationToken ct) =>
                WithPayment(paymentId, async p => ToHttp(http, await service.RefundAsync(
                    merchant.MerchantId!.Value, p, idempotencyKey, request, RefundChannel.MerchantApi, $"/v1/payments/{paymentId}/refunds", ct))))
            .WithTags("Merchant API: refunds")
            .RequireAuthorization(MerchantApiAuth.Policy)
            .WithValidation<CreateRefundRequest>();

        v1.MapPost("/payments/{paymentId}/reversal", (
                string paymentId, CreateReversalRequest request, [FromHeader(Name = IdempotencyKeys.HeaderName)] string? idempotencyKey,
                ICurrentMerchant merchant, RefundService service, HttpContext http, CancellationToken ct) =>
                WithPayment(paymentId, async p => ToHttp(http, await service.ReverseAsync(
                    merchant.MerchantId!.Value, p, idempotencyKey, request, $"/v1/payments/{paymentId}/reversal", ct))))
            .WithTags("Merchant API: refunds")
            .RequireAuthorization(MerchantApiAuth.Policy)
            .WithValidation<CreateReversalRequest>();

        v1.MapGet("/payments/{paymentId}/refunds", (string paymentId, ICurrentMerchant merchant, RefundQueries queries, CancellationToken ct) =>
                WithPayment(paymentId, async p => (await queries.ForPaymentAsync(merchant.MerchantId!.Value, p, ct)).ToHttp()))
            .WithTags("Merchant API: refunds")
            .RequireAuthorization(MerchantApiAuth.Policy);

        v1.MapGet("/refunds/{refundId}", async (string refundId, ICurrentMerchant merchant, RefundQueries queries, CancellationToken ct) =>
                PublicId.TryParse(refundId, IdPrefixes.Refund, out var id)
                    ? (await queries.GetAsync(merchant.MerchantId!.Value, id.Value, ct)).ToHttp()
                    : ErrorResults.Problem(Error.NotFound("refund_not_found", "Refund not found.")))
            .WithTags("Merchant API: refunds")
            .RequireAuthorization(MerchantApiAuth.Policy);
    }

    public static void MapDashboard(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/merchants/{merchantId}/payments/{paymentId}").WithTags("Refunds");

        group.MapGet("/refunds", (string merchantId, string paymentId, RefundQueries queries, CancellationToken ct) =>
                WithIds(merchantId, paymentId, async (m, p) => (await queries.ForPaymentAsync(m, p, ct)).ToHttp()))
            .RequireAuthorization(AuthPolicies.MerchantMember);

        // MerchantAdmins refund without a per-refund limit; MerchantOperators up to the merchant's operator limit (R6).
        group.MapPost("/refunds", (
                string merchantId, string paymentId, CreateRefundRequest request, [FromHeader(Name = IdempotencyKeys.HeaderName)] string? idempotencyKey,
                ICurrentUser user, RefundService service, HttpContext http, CancellationToken ct) =>
                WithIds(merchantId, paymentId, async (m, p) =>
                {
                    var channel = Channel(user, m);
                    return channel is null
                        ? MerchantUsersOnly()
                        : ToHttp(http, await service.RefundAsync(m, p, idempotencyKey, request, channel.Value, $"/api/merchants/{merchantId}/payments/{paymentId}/refunds", ct));
                }))
            .RequireAuthorization(AuthPolicies.MerchantMember)
            .WithValidation<CreateRefundRequest>();

        group.MapPost("/reversal", (
                string merchantId, string paymentId, CreateReversalRequest request, [FromHeader(Name = IdempotencyKeys.HeaderName)] string? idempotencyKey,
                ICurrentUser user, RefundService service, HttpContext http, CancellationToken ct) =>
                WithIds(merchantId, paymentId, async (m, p) =>
                    Channel(user, m) != RefundChannel.MerchantAdmin
                        ? MerchantUsersOnly()
                        : ToHttp(http, await service.ReverseAsync(m, p, idempotencyKey, request, $"/api/merchants/{merchantId}/payments/{paymentId}/reversal", ct))))
            .RequireAuthorization(AuthPolicies.MerchantAdmin)
            .WithValidation<CreateReversalRequest>();
    }

    /// <summary>
    /// Refunds and reversals are the merchant's decision: PlatformAdmins can view payments but do not
    /// move a merchant's money (they pass the merchant policies for investigation only).
    /// </summary>
    private static RefundChannel? Channel(ICurrentUser user, Guid merchantId) =>
        user.HasMerchantRole(merchantId, MerchantRoles.MerchantAdmin) ? RefundChannel.MerchantAdmin
        : user.HasMerchantRole(merchantId, MerchantRoles.MerchantOperator) ? RefundChannel.MerchantOperator
        : null;

    private static IResult MerchantUsersOnly() =>
        ErrorResults.Problem(Error.Forbidden("merchant_users_only", "Only the merchant's own users can move its money."));

    private static IResult ToHttp(HttpContext http, Result<IdempotentResponse> result)
    {
        if (!result.IsSuccess)
        {
            if (result.Error.Code == "idempotency_request_in_progress")
            {
                http.Response.Headers.RetryAfter = "1";
            }

            return ErrorResults.Problem(result.Error);
        }

        var response = result.Value;
        if (response.Replayed)
        {
            http.Response.Headers["Idempotent-Replayed"] = "true";
        }

        if (response.Location is not null)
        {
            http.Response.Headers.Location = response.Location;
        }

        return Results.Text(response.Body, "application/json", Encoding.UTF8, response.StatusCode);
    }

    private static async Task<IResult> WithPayment(string paymentId, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(paymentId, IdPrefixes.Payment, out var id) ? await handler(id.Value) : ErrorResults.Problem(PaymentNotFound);

    private static async Task<IResult> WithIds(string merchantId, string paymentId, Func<Guid, Guid, Task<IResult>> handler)
    {
        if (!PublicId.TryParse(merchantId, IdPrefixes.Merchant, out var m))
        {
            return ErrorResults.Problem(Error.NotFound("merchant_not_found", "Merchant not found."));
        }

        return PublicId.TryParse(paymentId, IdPrefixes.Payment, out var p) ? await handler(m.Value, p.Value) : ErrorResults.Problem(PaymentNotFound);
    }
}
