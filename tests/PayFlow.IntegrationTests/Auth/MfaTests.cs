using System.Buffers.Binary;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PayFlow.Infrastructure.Persistence;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Identity.Application;
using PayFlow.Modules.Identity.Contracts;

namespace PayFlow.IntegrationTests.Auth;

/// <summary>
/// Multi-factor sign-in (SECURITY.md, SEC-2): platform admins cannot get a session without an
/// authenticator; codes work once; failures count towards the lock-out; merchant users may opt in.
/// Runs with the production setting (MFA required for platform admins) and its own admin users.
/// </summary>
public sealed class MfaTests(PayFlowTestHost host)
{
    private static readonly Dictionary<string, string?> Required = new() { ["Security:Mfa:RequiredForPlatformAdmins"] = "true" };
    private const string AdminPassword = "mfa-admin-passphrase-123";

    [Fact]
    public async Task A_platform_admin_gets_no_session_until_an_authenticator_is_set_up()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(Required, clock);
        var (email, userId) = await CreatePlatformAdminAsync(factory);
        using var client = host.CreateDashboardClient(factory);

        var login = await client.LoginAsync(email, AdminPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await DashboardClient.ReadJsonAsync(login)).GetProperty("next").GetString().ShouldBe("mfa_enrollment");

        // A verified password alone opens nothing.
        (await client.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await client.Http.GetAsync(new Uri("/api/merchants", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);

        var enrollment = await DashboardClient.ReadJsonAsync(await client.PostAsync("/api/auth/mfa/enrollment", null));
        var secret = enrollment.GetProperty("secret").GetString()!;
        enrollment.GetProperty("provisioningUri").GetString()!.ShouldStartWith("otpauth://totp/PayFlow:");
        enrollment.GetProperty("provisioningUri").GetString()!.ShouldContain($"secret={secret}");

        // The secret is stored only encrypted.
        await using (var db = await host.OpenOwnerConnectionAsync())
        {
            await using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT pending_secret_ciphertext FROM identity.user_mfa WHERE user_id = @u";
            cmd.Parameters.AddWithValue("u", userId);
            var stored = (string)(await cmd.ExecuteScalarAsync())!;
            stored.ShouldNotContain(secret);
        }

        var wrong = await client.PostAsync("/api/auth/mfa/enrollment/confirm", new { code = WrongCode(secret, clock) });
        wrong.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DashboardClient.ReadJsonAsync(wrong)).GetProperty("code").GetString().ShouldBe("mfa_code_invalid");

        var confirmed = await client.PostAsync("/api/auth/mfa/enrollment/confirm", new { code = TestTotp.Code(secret, clock.GetUtcNow()) });
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await DashboardClient.ReadJsonAsync(confirmed);
        body.GetProperty("recoveryCodes").GetArrayLength().ShouldBe(MfaService.RecoveryCodeCount);
        body.GetProperty("user").GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
        body.GetProperty("user").GetProperty("mfaEnabled").GetBoolean().ShouldBeTrue();

        (await client.Http.GetAsync(new Uri("/api/merchants", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        factory.Logs.Contains(secret).ShouldBeFalse(); // never logged
    }

    [Fact]
    public async Task Each_code_works_once_and_failed_codes_lock_the_account()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(Required, clock);
        var (email, _) = await CreatePlatformAdminAsync(factory);
        var (secret, _) = await EnrolAsync(factory, email, clock);

        // The code used to enrol cannot sign in again within its 30 seconds...
        using (var replay = host.CreateDashboardClient(factory))
        {
            (await replay.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await replay.PostAsync("/api/auth/mfa/verify", new { code = TestTotp.Code(secret, clock.GetUtcNow()) })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // ...the next step's code can, once.
        clock.Advance(TimeSpan.FromSeconds(30));
        var code = TestTotp.Code(secret, clock.GetUtcNow());
        using (var next = host.CreateDashboardClient(factory))
        {
            (await next.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            var verified = await next.PostAsync("/api/auth/mfa/verify", new { code });
            verified.StatusCode.ShouldBe(HttpStatusCode.OK);
            (await DashboardClient.ReadJsonAsync(verified)).GetProperty("email").GetString().ShouldBe(email);
        }

        using (var again = host.CreateDashboardClient(factory))
        {
            (await again.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await again.PostAsync("/api/auth/mfa/verify", new { code })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        // Five failures lock the account (the replayed code above was the first): a correct password
        // between guesses resets nothing, and once locked even the password is refused.
        using var attacker = host.CreateDashboardClient(factory);
        for (var i = 0; i < 4; i++)
        {
            (await attacker.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await attacker.PostAsync("/api/auth/mfa/verify", new { code = WrongCode(secret, clock) })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        (await attacker.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized); // locked out
    }

    [Fact]
    public async Task A_recovery_code_signs_in_once()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(Required, clock);
        var (email, _) = await CreatePlatformAdminAsync(factory);
        var (_, recoveryCodes) = await EnrolAsync(factory, email, clock);

        using (var first = host.CreateDashboardClient(factory))
        {
            (await first.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await first.PostAsync("/api/auth/mfa/verify", new { recoveryCode = recoveryCodes[3].ToUpperInvariant() })).StatusCode.ShouldBe(HttpStatusCode.OK);
            var status = await DashboardClient.ReadJsonAsync(await first.Http.GetAsync(new Uri("/api/auth/mfa", UriKind.Relative)));
            status.GetProperty("recoveryCodesRemaining").GetInt32().ShouldBe(MfaService.RecoveryCodeCount - 1);
        }

        using var second = host.CreateDashboardClient(factory);
        (await second.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await second.PostAsync("/api/auth/mfa/verify", new { recoveryCode = recoveryCodes[3] })).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task A_verified_password_waits_five_minutes_for_its_code()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(Required, clock);
        var (email, _) = await CreatePlatformAdminAsync(factory);
        var (secret, _) = await EnrolAsync(factory, email, clock);
        using var client = host.CreateDashboardClient(factory);

        (await client.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        clock.Advance(TimeSpan.FromMinutes(6));
        var late = await client.PostAsync("/api/auth/mfa/verify", new { code = TestTotp.Code(secret, clock.GetUtcNow()) });
        late.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DashboardClient.ReadJsonAsync(late)).GetProperty("code").GetString().ShouldBe("mfa_sign_in_expired");
    }

    [Fact]
    public async Task A_platform_admin_cannot_remove_their_authenticator()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(Required, clock);
        var (email, _) = await CreatePlatformAdminAsync(factory);
        var (secret, _, client) = await EnrolAndKeepSessionAsync(factory, email, clock);
        using var _ = client;

        clock.Advance(TimeSpan.FromSeconds(30));
        var disable = await client.PostAsync("/api/auth/mfa/disable", new { code = TestTotp.Code(secret, clock.GetUtcNow()) });
        disable.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DashboardClient.ReadJsonAsync(disable)).GetProperty("code").GetString().ShouldBe("mfa_required");
    }

    [Fact]
    public async Task A_merchant_user_can_turn_mfa_on_and_off()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(clock: clock); // merchant users: MFA optional either way
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);

        var secret = (await DashboardClient.ReadJsonAsync(await m.Owner.PostAsync("/api/auth/mfa/enrollment", null))).GetProperty("secret").GetString()!;
        var confirmed = await m.Owner.PostAsync("/api/auth/mfa/enrollment/confirm", new { code = TestTotp.Code(secret, clock.GetUtcNow()) });
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);

        // The session that enrolled stays signed in; a new sign-in now needs a code.
        var me = await DashboardClient.ReadJsonAsync(await m.Owner.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative)));
        me.GetProperty("mfaEnabled").GetBoolean().ShouldBeTrue();
        using (var fresh = host.CreateDashboardClient(factory))
        {
            var login = await fresh.LoginAsync(m.Merchant.AdminEmail, m.Merchant.AdminPassword);
            login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
            (await DashboardClient.ReadJsonAsync(login)).GetProperty("next").GetString().ShouldBe("mfa");
        }

        clock.Advance(TimeSpan.FromSeconds(30));
        (await m.Owner.PostAsync("/api/auth/mfa/disable", new { code = TestTotp.Code(secret, clock.GetUtcNow()) })).StatusCode.ShouldBe(HttpStatusCode.NoContent);
        using var plain = host.CreateDashboardClient(factory);
        (await plain.LoginAsync(m.Merchant.AdminEmail, m.Merchant.AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task A_seeded_admin_with_a_configured_secret_signs_in_with_mfa_from_the_start()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var email = $"seeded-{Guid.NewGuid():N}@payflow.test";
        var secret = Base32.Encode(RandomNumberGenerator.GetBytes(20));
        await using var factory = host.CreateFactory(new Dictionary<string, string?>(Required)
        {
            ["Seed:PlatformAdmin:Email"] = email,
            ["Seed:PlatformAdmin:Password"] = AdminPassword,
            ["Seed:PlatformAdmin:TotpSecret"] = secret,
        }, clock);
        using var client = host.CreateDashboardClient(factory);

        var login = await client.LoginAsync(email, AdminPassword);
        login.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        (await DashboardClient.ReadJsonAsync(login)).GetProperty("next").GetString().ShouldBe("mfa");
        var verified = await client.PostAsync("/api/auth/mfa/verify", new { code = TestTotp.Code(secret, clock.GetUtcNow()) });
        verified.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DashboardClient.ReadJsonAsync(verified)).GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
        factory.Logs.Contains(secret).ShouldBeFalse();
    }

    private static async Task<(string Email, Guid UserId)> CreatePlatformAdminAsync(PayFlowApiFactory factory)
    {
        // Setup, not under test: platform admins other than the seeded one are created directly.
        await using var scope = factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<PayFlowUser>>();
        var email = $"admin-{Guid.NewGuid():N}@payflow.test";
        var user = new PayFlowUser
        {
            Id = Guid.CreateVersion7(),
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            DisplayName = "MFA test admin",
            CreatedAt = DateTimeOffset.UtcNow,
        };
        (await users.CreateAsync(user, AdminPassword)).Succeeded.ShouldBeTrue();
        (await users.AddToRoleAsync(user, PlatformRoles.PlatformAdmin)).Succeeded.ShouldBeTrue();
        return (email, user.Id);
    }

    private async Task<(string Secret, List<string> RecoveryCodes)> EnrolAsync(PayFlowApiFactory factory, string email, FakeTimeProvider clock)
    {
        var (secret, codes, client) = await EnrolAndKeepSessionAsync(factory, email, clock);
        client.Dispose();
        return (secret, codes);
    }

    private async Task<(string Secret, List<string> RecoveryCodes, DashboardClient Client)> EnrolAndKeepSessionAsync(PayFlowApiFactory factory, string email, FakeTimeProvider clock)
    {
        var client = host.CreateDashboardClient(factory);
        (await client.LoginAsync(email, AdminPassword)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        var secret = (await DashboardClient.ReadJsonAsync(await client.PostAsync("/api/auth/mfa/enrollment", null))).GetProperty("secret").GetString()!;
        var confirmed = await client.PostAsync("/api/auth/mfa/enrollment/confirm", new { code = TestTotp.Code(secret, clock.GetUtcNow()) });
        confirmed.StatusCode.ShouldBe(HttpStatusCode.OK);
        var codes = (await DashboardClient.ReadJsonAsync(confirmed)).GetProperty("recoveryCodes").EnumerateArray().Select(e => e.GetString()!).ToList();
        return (secret, codes, client);
    }

    /// <summary>A six-digit code that is valid for none of the accepted steps.</summary>
    private static string WrongCode(string secret, FakeTimeProvider clock)
    {
        var valid = Enumerable.Range(-1, 3).Select(d => TestTotp.Code(secret, clock.GetUtcNow().AddSeconds(30 * d))).ToHashSet();
        return Enumerable.Range(0, 1_000_000).Select(n => n.ToString("D6", CultureInfo.InvariantCulture)).First(c => !valid.Contains(c));
    }
}

/// <summary>An independent RFC 6238 implementation, so the tests do not trust the code under test.</summary>
internal static class TestTotp
{
    public static string Code(string base32Secret, DateTimeOffset at)
    {
        var key = Base32.Decode(base32Secret)!;
        var counter = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, at.ToUnixTimeSeconds() / 30);
#pragma warning disable CA5350 // RFC 6238 uses HMAC-SHA1.
        var hash = HMACSHA1.HashData(key, counter);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var value = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        return (value % 1_000_000).ToString("D6", CultureInfo.InvariantCulture);
    }
}
