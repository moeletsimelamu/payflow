using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Identity.Application;

/// <summary>Reads the tenant context from the authenticated principal's claims.</summary>
internal sealed class CurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    public bool IsAuthenticated =>
        Principal?.Identity?.IsAuthenticated == true &&
        Principal.FindFirstValue(PayFlowClaimTypes.ActorType) == nameof(ActorType.User);

    public Guid? UserId =>
        IsAuthenticated && Guid.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    public bool IsPlatformAdmin => IsAuthenticated && Principal!.IsInRole(PlatformRoles.PlatformAdmin);

    public IReadOnlyList<MerchantMembershipInfo> Memberships => IsAuthenticated ? ParseMemberships(Principal!) : [];

    public bool HasMerchantRole(Guid merchantId, params string[] roles) =>
        Memberships.Any(m => m.MerchantId == merchantId && (roles.Length == 0 || roles.Contains(m.Role, StringComparer.Ordinal)));

    internal static IReadOnlyList<MerchantMembershipInfo> ParseMemberships(ClaimsPrincipal principal)
    {
        var result = new List<MerchantMembershipInfo>();
        foreach (var claim in principal.FindAll(PayFlowClaimTypes.Membership))
        {
            var separator = claim.Value.IndexOf(':', StringComparison.Ordinal);
            if (separator > 0 &&
                Guid.TryParse(claim.Value.AsSpan(0, separator), out var merchantId) &&
                MerchantRoles.All.Contains(claim.Value[(separator + 1)..], StringComparer.Ordinal))
            {
                result.Add(new MerchantMembershipInfo(merchantId, claim.Value[(separator + 1)..]));
            }
        }

        return result;
    }
}
