using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Security;
using PayFlow.Modules.Identity.Domain;
using PayFlow.SharedKernel.Ids;

namespace PayFlow.Modules.Identity.Application;

/// <summary>Bound from <c>Security:Mfa</c>.</summary>
public sealed class MfaOptions
{
    public const string Section = "Security:Mfa";

    /// <summary>
    /// Platform admins must sign in with an authenticator (ASVS 5.0 Level 2, V6). On by default;
    /// only the integration-test host turns it off, for the shared seeded admin that hundreds of
    /// parallel tests sign in as (a code is accepted once only).
    /// </summary>
    public bool RequiredForPlatformAdmins { get; set; } = true;

    /// <summary>Name shown in the authenticator app.</summary>
    public string Issuer { get; set; } = "PayFlow";

    /// <summary>How long a password-verified sign-in waits for its code or its enrolment.</summary>
    public TimeSpan PendingSignInLifetime { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed record MfaEnrollment(string Secret, string ProvisioningUri);

/// <summary>Enrolment, verification and recovery codes for authenticator (TOTP) sign-in.</summary>
public sealed class MfaService(PayFlowDbContext db, SecretProtector protector, TimeProvider clock, IdGenerator ids, IOptions<MfaOptions> options)
{
    public const string SecretPurpose = "PayFlow.Identity.TotpSecret";
    public const int RecoveryCodeCount = 10;

    public Task<bool> IsEnabledAsync(Guid userId, CancellationToken ct) =>
        db.Set<UserMfa>().AnyAsync(m => m.UserId == userId && m.EnabledAt != null, ct);

    /// <summary>Creates a new secret, kept pending until <see cref="ConfirmEnrollmentAsync"/>.</summary>
    public async Task<MfaEnrollment> StartEnrollmentAsync(Guid userId, string account, CancellationToken ct)
    {
        var secret = Totp.NewSecret();
        var mfa = await db.Set<UserMfa>().SingleOrDefaultAsync(m => m.UserId == userId, ct);
        if (mfa is null)
        {
            mfa = new UserMfa { UserId = userId };
            db.Set<UserMfa>().Add(mfa);
        }

        mfa.PendingSecretCiphertext = protector.Protect(Base32.Encode(secret), SecretPurpose);
        mfa.PendingCreatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new MfaEnrollment(Base32.Encode(secret), Totp.ProvisioningUri(options.Value.Issuer, account, secret));
    }

    /// <summary>Activates the pending secret if <paramref name="code"/> matches it; returns new recovery codes, or null.</summary>
    public async Task<IReadOnlyList<string>?> ConfirmEnrollmentAsync(Guid userId, string code, CancellationToken ct)
    {
        var mfa = await db.Set<UserMfa>().SingleOrDefaultAsync(m => m.UserId == userId, ct);
        if (mfa?.PendingSecretCiphertext is null || mfa.PendingCreatedAt is not { } started ||
            clock.GetUtcNow() - started > TimeSpan.FromMinutes(30))
        {
            return null;
        }

        var secret = Base32.Decode(protector.Unprotect(mfa.PendingSecretCiphertext, SecretPurpose))!;
        if (Totp.Match(secret, code, clock.GetUtcNow(), lastUsedStep: 0) is not { } step)
        {
            return null;
        }

        mfa.SecretCiphertext = mfa.PendingSecretCiphertext;
        mfa.PendingSecretCiphertext = null;
        mfa.PendingCreatedAt = null;
        mfa.EnabledAt = clock.GetUtcNow();
        mfa.LastUsedStep = step;
        var codes = await ReplaceRecoveryCodesAsync(userId, ct);
        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>Enables MFA with a known secret (the seeded platform admin's, from configuration).</summary>
    public async Task ProvisionAsync(Guid userId, byte[] secret, CancellationToken ct)
    {
        var mfa = await db.Set<UserMfa>().SingleOrDefaultAsync(m => m.UserId == userId, ct);
        if (mfa is null)
        {
            mfa = new UserMfa { UserId = userId };
            db.Set<UserMfa>().Add(mfa);
        }

        mfa.SecretCiphertext = protector.Protect(Base32.Encode(secret), SecretPurpose);
        mfa.PendingSecretCiphertext = null;
        mfa.PendingCreatedAt = null;
        mfa.EnabledAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
    }

    /// <summary>
    /// Checks an authenticator code, or else a recovery code, and consumes it: a code is accepted
    /// once only, even by two requests racing with it.
    /// </summary>
    public async Task<MfaCheck> VerifyAsync(Guid userId, string? code, string? recoveryCode, CancellationToken ct)
    {
        var mfa = await db.Set<UserMfa>().AsNoTracking().SingleOrDefaultAsync(m => m.UserId == userId && m.EnabledAt != null, ct);
        if (mfa?.SecretCiphertext is null)
        {
            return MfaCheck.Failed;
        }

        if (!string.IsNullOrWhiteSpace(code))
        {
            var secret = Base32.Decode(protector.Unprotect(mfa.SecretCiphertext, SecretPurpose))!;
            if (Totp.Match(secret, code, clock.GetUtcNow(), mfa.LastUsedStep) is not { } step)
            {
                return MfaCheck.Failed;
            }

            var claimed = await db.Database.ExecuteSqlAsync(
                $"UPDATE identity.user_mfa SET last_used_step = {step} WHERE user_id = {userId} AND last_used_step < {step}", ct);
            return claimed == 1 ? MfaCheck.Authenticator : MfaCheck.Failed;
        }

        if (!string.IsNullOrWhiteSpace(recoveryCode))
        {
            var normalized = NormalizeRecoveryCode(recoveryCode);
            var candidates = await db.Set<MfaRecoveryCode>().AsNoTracking()
                .Where(c => c.UserId == userId && c.UsedAt == null)
                .ToListAsync(ct);
            var match = candidates.FirstOrDefault(c => CryptographicOperations.FixedTimeEquals(c.Hash, HashRecoveryCode(c.Salt, normalized)));
            if (match is null)
            {
                return MfaCheck.Failed;
            }

            var now = clock.GetUtcNow();
            var used = await db.Database.ExecuteSqlAsync(
                $"UPDATE identity.mfa_recovery_codes SET used_at = {now} WHERE id = {match.Id} AND used_at IS NULL", ct);
            return used == 1 ? MfaCheck.RecoveryCode : MfaCheck.Failed;
        }

        return MfaCheck.Failed;
    }

    public Task<int> RemainingRecoveryCodesAsync(Guid userId, CancellationToken ct) =>
        db.Set<MfaRecoveryCode>().CountAsync(c => c.UserId == userId && c.UsedAt == null, ct);

    /// <summary>Replaces every recovery code; the caller must already have verified a code.</summary>
    public async Task<IReadOnlyList<string>> RegenerateRecoveryCodesAsync(Guid userId, CancellationToken ct)
    {
        var codes = await ReplaceRecoveryCodesAsync(userId, ct);
        await db.SaveChangesAsync(ct);
        return codes;
    }

    /// <summary>Removes the authenticator and recovery codes; the caller must already have verified a code.</summary>
    public async Task DisableAsync(Guid userId, CancellationToken ct)
    {
        await db.Set<MfaRecoveryCode>().Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        await db.Set<UserMfa>().Where(m => m.UserId == userId).ExecuteDeleteAsync(ct);
    }

    private async Task<IReadOnlyList<string>> ReplaceRecoveryCodesAsync(Guid userId, CancellationToken ct)
    {
        await db.Set<MfaRecoveryCode>().Where(c => c.UserId == userId).ExecuteDeleteAsync(ct);
        var now = clock.GetUtcNow();
        var codes = new List<string>(RecoveryCodeCount);
        for (var i = 0; i < RecoveryCodeCount; i++)
        {
            var code = NewRecoveryCode();
            var salt = RandomNumberGenerator.GetBytes(16);
            db.Set<MfaRecoveryCode>().Add(new MfaRecoveryCode
            {
                Id = ids.NewId(),
                UserId = userId,
                Salt = salt,
                Hash = HashRecoveryCode(salt, NormalizeRecoveryCode(code)),
                CreatedAt = now,
            });
            codes.Add(code);
        }

        return codes;
    }

    /// <summary>20 Base32 characters (100 bits) in groups of five, e.g. <c>k7q2m-...</c>.</summary>
    internal static string NewRecoveryCode()
    {
        var text = Base32.Encode(RandomNumberGenerator.GetBytes(13))[..20].ToLowerInvariant();
        return string.Join('-', Enumerable.Range(0, 4).Select(i => text.Substring(i * 5, 5)));
    }

    internal static string NormalizeRecoveryCode(string code) =>
        new(code.Where(char.IsAsciiLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    private static byte[] HashRecoveryCode(byte[] salt, string normalized) =>
        SHA256.HashData([.. salt, .. Encoding.ASCII.GetBytes(normalized)]);
}

public enum MfaCheck
{
    Failed,
    Authenticator,
    RecoveryCode,
}
