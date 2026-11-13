using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Ids;

namespace PayFlow.Modules.Identity.Application;

/// <summary>
/// Requires the user to hold one of <see cref="Roles"/> in the merchant named by the
/// <c>{merchantId}</c> route value (a <c>mer_...</c> public id). PlatformAdmins always pass.
/// </summary>
public sealed class MerchantAccessRequirement(params string[] roles) : IAuthorizationRequirement
{
    public const string RouteValueName = "merchantId";

    public IReadOnlyList<string> Roles { get; } = roles;
}

internal sealed class MerchantAccessHandler(ICurrentUser currentUser) : AuthorizationHandler<MerchantAccessRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, MerchantAccessRequirement requirement)
    {
        if (!currentUser.IsAuthenticated)
        {
            return Task.CompletedTask;
        }

        if (currentUser.IsPlatformAdmin)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        var http = context.Resource as HttpContext;
        var routeValue = http?.GetRouteValue(MerchantAccessRequirement.RouteValueName) as string;
        if (PublicId.TryParse(routeValue, IdPrefixes.Merchant, out var merchantId) &&
            currentUser.HasMerchantRole(merchantId.Value, [.. requirement.Roles]))
        {
            context.Succeed(requirement);
        }

        // Not succeeding results in 403. Endpoints additionally scope every query by merchant id,
        // so a missing or foreign merchant is never disclosed beyond "forbidden".
        return Task.CompletedTask;
    }
}
