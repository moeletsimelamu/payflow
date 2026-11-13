using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Audit.Contracts;

/// <summary>
/// An auditable action. <see cref="Action"/> is a dotted, past-tense name such as
/// <c>identity.login.succeeded</c> or <c>merchant.api_key.rotated</c>.
/// </summary>
public sealed record AuditEntry(string Action, string TargetType, string? TargetId)
{
    public Guid? MerchantId { get; init; }

    /// <summary>Before/after values or context. Sensitive fields are redacted before storage.</summary>
    public object? Changes { get; init; }

    /// <summary>Overrides the ambient actor, for example the user who is in the middle of logging in.</summary>
    public Actor? Actor { get; init; }
}

/// <summary>
/// Records audit events into the current unit of work, so the audit record commits atomically
/// with the change it describes. Call <c>SaveChangesAsync</c> as part of the normal commit.
/// </summary>
public interface IAuditLog
{
    void Record(AuditEntry entry);
}
