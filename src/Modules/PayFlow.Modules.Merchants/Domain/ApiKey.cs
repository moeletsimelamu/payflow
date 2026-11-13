namespace PayFlow.Modules.Merchants.Domain;

public enum ApiKeyState
{
    Active,
    Expiring,
    Expired,
    Revoked,
}

/// <summary>
/// A merchant API credential. Only the public <see cref="Prefix"/> and an HMAC of the full key are
/// stored; the secret is shown once at creation and cannot be recovered.
/// </summary>
internal sealed class ApiKey
{
    private ApiKey()
    {
    }

    public Guid Id { get; private init; }

    public Guid MerchantId { get; private init; }

    public string Name { get; private set; } = string.Empty;

    /// <summary>8-character lookup prefix, unique and safe to display or log.</summary>
    public string Prefix { get; private init; } = string.Empty;

    public byte[] SecretHash { get; private init; } = [];

    public DateTimeOffset CreatedAt { get; private init; }

    public string CreatedBy { get; private init; } = string.Empty;

    public DateTimeOffset? LastUsedAt { get; private set; }

    /// <summary>Set when the key is rotated: it keeps working until then (grace period).</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    public DateTimeOffset? RevokedAt { get; private set; }

    /// <summary>The key that replaced this one on rotation.</summary>
    public Guid? ReplacedByKeyId { get; private set; }

    public static ApiKey Issue(Guid id, Guid merchantId, string name, string prefix, byte[] secretHash, string createdBy, DateTimeOffset now) => new()
    {
        Id = id,
        MerchantId = merchantId,
        Name = name.Trim(),
        Prefix = prefix,
        SecretHash = secretHash,
        CreatedBy = createdBy,
        CreatedAt = now,
    };

    public ApiKeyState StateAt(DateTimeOffset now) => this switch
    {
        { RevokedAt: not null } => ApiKeyState.Revoked,
        { ExpiresAt: { } expiry } when expiry <= now => ApiKeyState.Expired,
        { ExpiresAt: not null } => ApiKeyState.Expiring,
        _ => ApiKeyState.Active,
    };

    public bool IsUsableAt(DateTimeOffset now) => StateAt(now) is ApiKeyState.Active or ApiKeyState.Expiring;

    public void Revoke(DateTimeOffset now) => RevokedAt ??= now;

    /// <summary>Schedules expiry after the grace period. A shorter existing expiry is never extended.</summary>
    public void ReplaceWith(Guid replacementId, DateTimeOffset expiresAt)
    {
        ReplacedByKeyId = replacementId;
        if (ExpiresAt is null || expiresAt < ExpiresAt)
        {
            ExpiresAt = expiresAt;
        }
    }
}
