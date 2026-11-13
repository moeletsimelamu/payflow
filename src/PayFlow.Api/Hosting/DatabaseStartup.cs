using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Application;

namespace PayFlow.Api.Hosting;

/// <summary>
/// Schema migration and bootstrap seeding.
/// <list type="bullet">
/// <item><c>PayFlow.Api --migrate</c> applies migrations and exits. It is used by the Compose
/// <c>migrate</c> job with the DDL-capable <c>payflow_migrator</c> role.</item>
/// <item><c>Database:ApplyMigrationsOnStartup=true</c> migrates on start (local Development only).</item>
/// </list>
/// </summary>
public static class DatabaseStartup
{
    // Arbitrary, stable advisory-lock key reserved for bootstrap seeding.
    private const long SeedLockKey = 0x5041_5946_4C4F_5701; // "PAYFLOW" + 1

    public static async Task MigrateAsync(IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();
        await db.Database.MigrateAsync();
    }

    public static async Task InitialiseAsync(WebApplication app)
    {
        if (app.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup"))
        {
            await MigrateAsync(app.Services);
        }

        await using var scope = app.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<PayFlowDbContext>();

        // Several API instances may start at once. A session-level advisory lock on the connection
        // the Identity stores use serialises seeding, so only the first instance creates the admin.
        await db.Database.OpenConnectionAsync();
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_lock({SeedLockKey})");
            try
            {
                await scope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync();
                await ReKeyUnencryptedDataProtectionKeysAsync(scope.ServiceProvider, db);
            }
            finally
            {
                await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_unlock({SeedLockKey})");
            }
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    /// <summary>
    /// One-time upgrade for databases created before the key ring was encrypted (Phase 6): revoke every
    /// key stored in plaintext, so it can never again sign or open a cookie, and create a fresh key,
    /// which is encrypted with the master key. Existing dashboard sessions end once.
    /// </summary>
    private static async Task ReKeyUnencryptedDataProtectionKeysAsync(IServiceProvider services, PayFlowDbContext db)
    {
        var plaintextKeyIds = (await db.DataProtectionKeys.AsNoTracking()
                .Where(k => k.Xml != null && k.Xml.StartsWith("<key ") && !k.Xml.Contains("MasterKeyXmlDecryptor"))
                .Select(k => k.Xml!)
                .ToListAsync())
            .Select(xml => (Guid?)Guid.Parse(System.Xml.Linq.XElement.Parse(xml).Attribute("id")!.Value))
            .ToList();
        if (plaintextKeyIds.Count == 0)
        {
            return;
        }

        var keyManager = services.GetRequiredService<Microsoft.AspNetCore.DataProtection.KeyManagement.IKeyManager>();
        var revoked = keyManager.GetAllKeys().Where(k => k.IsRevoked).Select(k => k.KeyId).ToHashSet();
        var toRevoke = plaintextKeyIds.Where(id => !revoked.Contains(id!.Value)).Select(id => id!.Value).ToList();
        if (toRevoke.Count == 0)
        {
            return;
        }

        var now = services.GetRequiredService<TimeProvider>().GetUtcNow();
        keyManager.CreateNewKey(now, now.AddDays(90));
        foreach (var id in toRevoke)
        {
            keyManager.RevokeKey(id, "Re-keyed: key ring is now encrypted with the PayFlow master key.");
        }

        services.GetRequiredService<ILoggerFactory>().CreateLogger(typeof(DatabaseStartup))
            .LogWarning("Revoked {Count} unencrypted Data Protection key(s) and created an encrypted one; users must sign in again.", toRevoke.Count);
    }
}
