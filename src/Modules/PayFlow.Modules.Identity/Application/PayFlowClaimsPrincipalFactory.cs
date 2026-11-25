using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Identity.Application;

/// <summary>
/// Adds PayFlow claims (actor type, public id, display name, merchant memberships) to the cookie
/// principal. The security-stamp validator re-runs this every minute, so membership and role
/// changes take effect without a fresh login.
/// </summary>
internal sealed class PayFlowClaimsPrincipalFactory(
    UserManager<PayFlowUser> userManager,
    RoleManager<PayFlowRole> roleManager,
    IOptions<IdentityOptions> options,
    PayFlowDbContext db)
    : UserClaimsPrincipalFactory<PayFlowUser, PayFlowRole>(userManager, roleManager, options)
{
    protected override async Task<ClaimsIdentity> GenerateClaimsAsync(PayFlowUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        var identity = await base.GenerateClaimsAsync(user);
        identity.AddClaim(new Claim(PayFlowClaimTypes.ActorType, nameof(ActorType.User)));
        identity.AddClaim(new Claim(PayFlowClaimTypes.PublicId, PublicId.Create(IdPrefixes.User, user.Id)));
        identity.AddClaim(new Claim(PayFlowClaimTypes.DisplayName, user.DisplayName));
        if (user.TwoFactorEnabled)
        {
            identity.AddClaim(new Claim(PayFlowClaimTypes.MfaEnabled, "true"));
        }

        var memberships = await db.Set<MerchantMembership>()
            .AsNoTracking()
            .Where(m => m.UserId == user.Id)
            .Select(m => new { m.MerchantId, m.Role })
            .ToListAsync();
        foreach (var m in memberships)
        {
            identity.AddClaim(new Claim(
                PayFlowClaimTypes.Membership,
                string.Create(CultureInfo.InvariantCulture, $"{m.MerchantId:D}:{m.Role}")));
        }

        return identity;
    }
}
