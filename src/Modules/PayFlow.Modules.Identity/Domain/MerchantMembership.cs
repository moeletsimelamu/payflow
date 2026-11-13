namespace PayFlow.Modules.Identity.Domain;

/// <summary>
/// Grants a user a role within one merchant. The foreign key to <c>merchants.merchants</c> is added
/// in Phase 3 when the Merchants module exists.
/// </summary>
internal sealed class MerchantMembership
{
    public Guid UserId { get; init; }

    public Guid MerchantId { get; init; }

    public required string Role { get; set; }

    public DateTimeOffset CreatedAt { get; init; }
}
