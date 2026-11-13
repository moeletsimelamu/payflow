using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Identity.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Identity.Application;

internal sealed class MerchantMembershipAdmin(
    PayFlowDbContext db,
    UserManager<PayFlowUser> users,
    IdGenerator ids,
    TimeProvider clock) : IMerchantMembershipAdmin
{
    public async Task<IReadOnlyList<MerchantMemberInfo>> ListAsync(Guid merchantId, CancellationToken cancellationToken) =>
        await (from m in db.Set<MerchantMembership>().AsNoTracking()
               join u in db.Users.AsNoTracking() on m.UserId equals u.Id
               where m.MerchantId == merchantId
               orderby m.CreatedAt
               select new MerchantMemberInfo(u.Id, u.Email!, u.DisplayName, m.Role, m.CreatedAt, u.PasswordHash != null))
            .ToListAsync(cancellationToken);

    public async Task<Result<MemberInvitation>> InviteAsync(Guid merchantId, string email, string displayName, string role, CancellationToken cancellationToken)
    {
        if (!MerchantRoles.All.Contains(role, StringComparer.Ordinal))
        {
            return Error.Validation("invalid_role", $"Role must be one of: {string.Join(", ", MerchantRoles.All)}.");
        }

        var user = await users.FindByEmailAsync(email);
        if (user is null)
        {
            user = new PayFlowUser
            {
                Id = ids.NewId(),
                UserName = email,
                Email = email,
                EmailConfirmed = true,
                DisplayName = displayName.Trim(),
                CreatedAt = clock.GetUtcNow(),
            };
            var created = await users.CreateAsync(user);
            if (!created.Succeeded)
            {
                return Error.Validation("invalid_user", string.Join(" ", created.Errors.Select(e => e.Description)));
            }
        }
        else if (await IsPlatformAdminAsync(user))
        {
            return Error.Unprocessable("platform_admin_membership", "Platform administrators already have access to every merchant.");
        }

        var exists = await db.Set<MerchantMembership>().AnyAsync(m => m.MerchantId == merchantId && m.UserId == user.Id, cancellationToken);
        if (exists)
        {
            return Error.Conflict("member_exists", "This user is already a member of the merchant.");
        }

        db.Set<MerchantMembership>().Add(new MerchantMembership
        {
            UserId = user.Id,
            MerchantId = merchantId,
            Role = role,
            CreatedAt = clock.GetUtcNow(),
        });

        // Only users who have never set a password get a token; existing users sign in as usual.
        var token = await users.HasPasswordAsync(user) ? null : await users.GeneratePasswordResetTokenAsync(user);
        return new MemberInvitation(user.Id, user.Email!, role, token);
    }

    public async Task<Result> ChangeRoleAsync(Guid merchantId, Guid userId, string role, CancellationToken cancellationToken)
    {
        if (!MerchantRoles.All.Contains(role, StringComparer.Ordinal))
        {
            return Error.Validation("invalid_role", $"Role must be one of: {string.Join(", ", MerchantRoles.All)}.");
        }

        var membership = await FindAsync(merchantId, userId, cancellationToken);
        if (membership is null)
        {
            return MemberNotFound();
        }

        if (membership.Role == MerchantRoles.MerchantAdmin && role != MerchantRoles.MerchantAdmin &&
            await IsLastAdminAsync(merchantId, cancellationToken))
        {
            return LastAdmin();
        }

        membership.Role = role;
        return Result.Success();
    }

    public async Task<Result> RemoveAsync(Guid merchantId, Guid userId, CancellationToken cancellationToken)
    {
        var membership = await FindAsync(merchantId, userId, cancellationToken);
        if (membership is null)
        {
            return MemberNotFound();
        }

        if (membership.Role == MerchantRoles.MerchantAdmin && await IsLastAdminAsync(merchantId, cancellationToken))
        {
            return LastAdmin();
        }

        db.Set<MerchantMembership>().Remove(membership);
        return Result.Success();
    }

    private Task<MerchantMembership?> FindAsync(Guid merchantId, Guid userId, CancellationToken ct) =>
        db.Set<MerchantMembership>().SingleOrDefaultAsync(m => m.MerchantId == merchantId && m.UserId == userId, ct);

    private async Task<bool> IsLastAdminAsync(Guid merchantId, CancellationToken ct) =>
        await db.Set<MerchantMembership>().CountAsync(m => m.MerchantId == merchantId && m.Role == MerchantRoles.MerchantAdmin, ct) <= 1;

    private async Task<bool> IsPlatformAdminAsync(PayFlowUser user) => await users.IsInRoleAsync(user, PlatformRoles.PlatformAdmin);

    private static Error MemberNotFound() => Error.NotFound("member_not_found", "No such member for this merchant.");

    private static Error LastAdmin() => Error.Unprocessable("last_merchant_admin", "A merchant must keep at least one MerchantAdmin.");
}
