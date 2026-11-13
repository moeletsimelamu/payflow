using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Identity.Contracts;

public sealed record MerchantMemberInfo(Guid UserId, string Email, string DisplayName, string Role, DateTimeOffset AddedAt, bool HasAcceptedInvitation);

/// <summary>
/// Result of inviting a user to a merchant. <see cref="InvitationToken"/> is set only when the user
/// still has to choose a password; it is shown once and must be passed to them out of band.
/// </summary>
public sealed record MemberInvitation(Guid UserId, string Email, string Role, [property: Sensitive] string? InvitationToken);

/// <summary>Manages who can act for a merchant. Implemented by the Identity module.</summary>
public interface IMerchantMembershipAdmin
{
    Task<IReadOnlyList<MerchantMemberInfo>> ListAsync(Guid merchantId, CancellationToken cancellationToken);

    /// <summary>
    /// Adds a membership, creating the user (without a password) if needed. Call it inside the
    /// caller's database transaction: creating a user writes immediately, and the membership is
    /// saved with the caller's own changes.
    /// </summary>
    Task<Result<MemberInvitation>> InviteAsync(Guid merchantId, string email, string displayName, string role, CancellationToken cancellationToken);

    Task<Result> ChangeRoleAsync(Guid merchantId, Guid userId, string role, CancellationToken cancellationToken);

    Task<Result> RemoveAsync(Guid merchantId, Guid userId, CancellationToken cancellationToken);
}
