using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Identity.Application;

/// <summary>Bootstrap configuration, bound from <c>Seed:PlatformAdmin</c>. Supplied via secrets, never appsettings.</summary>
public sealed class PlatformAdminSeedOptions
{
    public const string Section = "Seed:PlatformAdmin";

    public string? Email { get; set; }

    [Sensitive]
    public string? Password { get; set; }

    public string DisplayName { get; set; } = "Platform Administrator";

    /// <summary>
    /// Optional Base32 authenticator secret for this admin, so the operator (and automated test
    /// tools) can sign in with MFA from the first start. Without it the admin sets up an
    /// authenticator at first sign-in. Never replaces an authenticator that is already set up.
    /// </summary>
    [Sensitive]
    public string? TotpSecret { get; set; }
}

/// <summary>Creates the PlatformAdmin role and, if configured and absent, the first PlatformAdmin user.</summary>
public sealed class IdentitySeeder(
    RoleManager<PayFlowRole> roles,
    UserManager<PayFlowUser> users,
    IOptions<PlatformAdminSeedOptions> options,
    IdGenerator ids,
    TimeProvider clock,
    MfaService mfa,
    ILogger<IdentitySeeder> logger)
{
    public async Task SeedAsync()
    {
        if (!await roles.RoleExistsAsync(PlatformRoles.PlatformAdmin))
        {
            EnsureSucceeded(await roles.CreateAsync(new PayFlowRole { Id = ids.NewId(), Name = PlatformRoles.PlatformAdmin }), "create role");
        }

        var seed = options.Value;
        if (string.IsNullOrWhiteSpace(seed.Email) || string.IsNullOrEmpty(seed.Password))
        {
            logger.LogInformation("No PlatformAdmin seed credentials configured; skipping admin user seeding.");
            return;
        }

        if (await users.FindByEmailAsync(seed.Email) is { } existing)
        {
            await ProvisionAuthenticatorAsync(existing, seed);
            return;
        }

        var user = new PayFlowUser
        {
            Id = ids.NewId(),
            UserName = seed.Email,
            Email = seed.Email,
            EmailConfirmed = true,
            DisplayName = seed.DisplayName,
            CreatedAt = clock.GetUtcNow(),
        };
        EnsureSucceeded(await users.CreateAsync(user, seed.Password), "create PlatformAdmin user");
        EnsureSucceeded(await users.AddToRoleAsync(user, PlatformRoles.PlatformAdmin), "assign PlatformAdmin role");
        logger.LogInformation("Seeded PlatformAdmin user {UserId}.", PublicId.Create(IdPrefixes.User, user.Id));
        await ProvisionAuthenticatorAsync(user, seed);
    }

    private async Task ProvisionAuthenticatorAsync(PayFlowUser user, PlatformAdminSeedOptions seed)
    {
        if (string.IsNullOrWhiteSpace(seed.TotpSecret) || await mfa.IsEnabledAsync(user.Id, CancellationToken.None))
        {
            return;
        }

        var secret = Base32.Decode(seed.TotpSecret);
        if (secret is not { Length: >= 16 })
        {
            throw new InvalidOperationException("Seed:PlatformAdmin:TotpSecret must be Base32 of at least 16 bytes (26+ characters).");
        }

        await mfa.ProvisionAsync(user.Id, secret, CancellationToken.None);
        EnsureSucceeded(await users.SetTwoFactorEnabledAsync(user, true), "enable two-factor for the PlatformAdmin");
        logger.LogInformation("Provisioned the configured authenticator for PlatformAdmin {UserId}.", PublicId.Create(IdPrefixes.User, user.Id));
    }

    private static void EnsureSucceeded(IdentityResult result, string operation)
    {
        if (!result.Succeeded)
        {
            // Identity error descriptions never contain the password itself.
            throw new InvalidOperationException(
                $"Identity seeding failed to {operation}: {string.Join("; ", result.Errors.Select(e => e.Description))}");
        }
    }
}
