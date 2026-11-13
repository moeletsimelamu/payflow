using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Application;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;
using static PayFlow.Modules.Merchants.Endpoints.MerchantEndpoints;

namespace PayFlow.Modules.Merchants.Endpoints;

internal static class ApiKeyAndMemberEndpoints
{
    private static readonly Error KeyNotFound = Error.NotFound("api_key_not_found", "API key not found.");
    private static readonly Error MemberNotFound = Error.NotFound("member_not_found", "No such member for this merchant.");

    public static void Map(IEndpointRouteBuilder app)
    {
        var keys = app.MapGroup("/api/merchants/{merchantId}/api-keys").WithTags("API keys").RequireAuthorization(AuthPolicies.MerchantAdmin);

        keys.MapGet("/", (string merchantId, ApiKeyService service, CancellationToken ct) =>
            WithMerchant(merchantId, async id => (await service.ListAsync(id, ct)).ToHttp()));

        keys.MapPost("/", (string merchantId, CreateApiKeyRequest request, ApiKeyService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.CreateAsync(id, request, ct))
                    .ToHttp(k => Results.Created($"/api/merchants/{merchantId}/api-keys/{k.Key.Id}", k))))
            .WithValidation<CreateApiKeyRequest>();

        keys.MapPost("/{keyId}/rotate", (string merchantId, string keyId, RotateApiKeyRequest request, ApiKeyService service, CancellationToken ct) =>
                WithIds(merchantId, keyId, IdPrefixes.ApiKey, KeyNotFound, async (m, k) => (await service.RotateAsync(m, k, request, ct))
                    .ToHttp(r => Results.Created($"/api/merchants/{merchantId}/api-keys/{r.Key.Id}", r))))
            .WithValidation<RotateApiKeyRequest>();

        keys.MapPost("/{keyId}/revoke", (string merchantId, string keyId, ApiKeyService service, CancellationToken ct) =>
            WithIds(merchantId, keyId, IdPrefixes.ApiKey, KeyNotFound, async (m, k) => (await service.RevokeAsync(m, k, ct)).ToHttp()));

        var members = app.MapGroup("/api/merchants/{merchantId}/members").WithTags("Members").RequireAuthorization(AuthPolicies.MerchantAdmin);

        members.MapGet("/", (string merchantId, MemberService service, CancellationToken ct) =>
            WithMerchant(merchantId, async id => (await service.ListAsync(id, ct)).ToHttp()));

        members.MapPost("/", (string merchantId, InviteMemberRequest request, MemberService service, CancellationToken ct) =>
                WithMerchant(merchantId, async id => (await service.InviteAsync(id, request, ct)).ToHttp(i => Results.Created($"/api/merchants/{merchantId}/members/{i.UserId}", i))))
            .WithValidation<InviteMemberRequest>();

        members.MapPatch("/{userId}", (string merchantId, string userId, ChangeMemberRoleRequest request, MemberService service, CancellationToken ct) =>
                WithIds(merchantId, userId, IdPrefixes.User, MemberNotFound, async (m, u) => (await service.ChangeRoleAsync(m, u, request, ct)).ToHttp(Results.NoContent)))
            .WithValidation<ChangeMemberRoleRequest>();

        members.MapDelete("/{userId}", (string merchantId, string userId, MemberService service, CancellationToken ct) =>
            WithIds(merchantId, userId, IdPrefixes.User, MemberNotFound, async (m, u) => (await service.RemoveAsync(m, u, ct)).ToHttp(Results.NoContent)));
    }
}
