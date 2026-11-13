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
}

/// <summary>Creates the PlatformAdmin role and, if configured and absent, the first PlatformAdmin user.</summary>
public sealed class IdentitySeeder(
    RoleManager<PayFlowRole> roles,
    UserManager<PayFlowUser> users,
    IOptions<PlatformAdminSeedOptions> options,
    IdGenerator ids,
    TimeProvider clock,
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

        if (await users.FindByEmailAsync(seed.Email) is not null)
        {
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
