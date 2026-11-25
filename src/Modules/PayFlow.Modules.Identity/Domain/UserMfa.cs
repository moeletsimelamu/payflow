namespace PayFlow.Modules.Identity.Domain;

/// <summary>
/// A user's authenticator (TOTP) state. The secret is needed in plaintext to compute codes, so it is
/// stored encrypted (AES-256-GCM under the master key, like webhook secrets).
/// </summary>
internal sealed class UserMfa
{
    public Guid UserId { get; init; }

    /// <summary>The active authenticator secret; null until enrolment is confirmed.</summary>
    public string? SecretCiphertext { get; set; }

    /// <summary>A secret shown to the user but not yet confirmed with a code.</summary>
    public string? PendingSecretCiphertext { get; set; }

    public DateTimeOffset? PendingCreatedAt { get; set; }

    public DateTimeOffset? EnabledAt { get; set; }

    /// <summary>The last time step whose code was accepted: each code works once only.</summary>
    public long LastUsedStep { get; set; }
}

/// <summary>A single-use recovery code, stored as a salted SHA-256 hash (the code itself is shown once).</summary>
internal sealed class MfaRecoveryCode
{
    public Guid Id { get; init; }

    public Guid UserId { get; init; }

    public required byte[] Salt { get; init; }

    public required byte[] Hash { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? UsedAt { get; set; }
}
