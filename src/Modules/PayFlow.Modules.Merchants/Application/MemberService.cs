using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Merchants.Application;

/// <summary>Merchant team management (MER-5) and activity history (MER-7).</summary>
internal sealed class MemberService(PayFlowDbContext db, IMerchantMembershipAdmin memberships, IAuditLog audit, IAuditQuery auditQuery)
{
    private const string Target = "user";

    public async Task<Result<IReadOnlyList<MemberResponse>>> ListAsync(Guid merchantId, CancellationToken ct)
    {
        if (!await MerchantExistsAsync(merchantId, ct))
        {
            return Mapping.MerchantNotFound();
        }

        var members = await memberships.ListAsync(merchantId, ct);
        return members.Select(m => new MemberResponse(PublicId.Create(IdPrefixes.User, m.UserId), m.Email, m.DisplayName, m.Role, m.AddedAt, m.HasAcceptedInvitation)).ToList();
    }

    public async Task<Result<MemberInvitationResponse>> InviteAsync(Guid merchantId, InviteMemberRequest request, CancellationToken ct)
    {
        var status = await db.Set<Merchant>().Where(m => m.Id == merchantId).Select(m => (MerchantStatus?)m.Status).SingleOrDefaultAsync(ct);
        if (status is null)
        {
            return Mapping.MerchantNotFound();
        }

        if (status == MerchantStatus.Closed)
        {
            return Error.Unprocessable("merchant_closed", "Closed merchants cannot add members.");
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var invited = await memberships.InviteAsync(merchantId, request.Email.Trim(), request.DisplayName, request.Role, ct);
        if (!invited.IsSuccess)
        {
            return invited.Error;
        }

        var response = MerchantService.ToResponse(invited.Value);
        audit.Record(new AuditEntry("merchant.member.invited", Target, response.UserId)
        {
            MerchantId = merchantId,
            Changes = new { email = response.Email, role = response.Role },
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return response;
    }

    public async Task<Result> ChangeRoleAsync(Guid merchantId, Guid userId, ChangeMemberRoleRequest request, CancellationToken ct)
    {
        var result = await memberships.ChangeRoleAsync(merchantId, userId, request.Role, ct);
        if (result.IsSuccess)
        {
            audit.Record(new AuditEntry("merchant.member.role_changed", Target, PublicId.Create(IdPrefixes.User, userId))
            {
                MerchantId = merchantId,
                Changes = new { role = request.Role },
            });
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public async Task<Result> RemoveAsync(Guid merchantId, Guid userId, CancellationToken ct)
    {
        var result = await memberships.RemoveAsync(merchantId, userId, ct);
        if (result.IsSuccess)
        {
            audit.Record(new AuditEntry("merchant.member.removed", Target, PublicId.Create(IdPrefixes.User, userId)) { MerchantId = merchantId });
            await db.SaveChangesAsync(ct);
        }

        return result;
    }

    public async Task<Result<ActivityResponse>> ActivityAsync(Guid merchantId, long? before, int limit, CancellationToken ct)
    {
        if (!await MerchantExistsAsync(merchantId, ct))
        {
            return Mapping.MerchantNotFound();
        }

        limit = Math.Clamp(limit, 1, 100);
        var items = await auditQuery.ListForMerchantAsync(merchantId, before, limit, ct);
        var response = items
            .Select(i => new ActivityItemResponse(i.Id, i.OccurredAt, i.ActorType, i.ActorId, i.ActorDisplay, i.Action, i.TargetType, i.TargetId, i.Changes, i.CorrelationId))
            .ToList();
        return new ActivityResponse(response, response.Count == limit ? response[^1].Id : null);
    }

    private Task<bool> MerchantExistsAsync(Guid merchantId, CancellationToken ct) => db.Set<Merchant>().AnyAsync(m => m.Id == merchantId, ct);
}
