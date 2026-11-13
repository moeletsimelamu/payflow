using System.Net;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Merchants;

public sealed class ApiKeyTests(PayFlowTestHost host)
{
    [Fact]
    public async Task A_new_key_authenticates_the_merchant_api_and_its_secret_is_never_stored_or_logged()
    {
        var (owner, merchant) = await OwnerAsync();
        using var _ = owner;

        var key = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);
        using var api = MerchantScenario.ApiClient(host.Factory, key.Secret);
        var profile = await MerchantScenario.GetMerchantProfileAsync(api);

        profile.StatusCode.ShouldBe(HttpStatusCode.OK);
        var body = await MerchantScenario.ReadAsync(profile);
        body.GetProperty("id").GetString().ShouldBe(merchant.Id);
        body.GetProperty("simulated").GetBoolean().ShouldBeTrue();
        profile.Headers.Contains("RateLimit-Remaining").ShouldBeTrue();

        // Listing shows metadata only.
        var list = await owner.Http.GetStringAsync(new Uri($"/api/merchants/{merchant.Id}/api-keys", UriKind.Relative));
        list.ShouldContain(key.Prefix);
        list.ShouldNotContain(key.Secret);

        // Only a 32-byte HMAC is stored, and the secret appears in no table or log line.
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var stored = new NpgsqlCommand("SELECT length(secret_hash) FROM merchants.api_keys WHERE prefix = @p", db);
        stored.Parameters.AddWithValue("p", key.Prefix);
        ((int)(await stored.ExecuteScalarAsync())!).ShouldBe(32);

        await using var leaked = new NpgsqlCommand(
            """
            SELECT (SELECT count(*) FROM audit.audit_events WHERE changes::text LIKE '%' || @s || '%')
                 + (SELECT count(*) FROM merchants.api_keys WHERE name LIKE '%' || @s || '%' OR created_by LIKE '%' || @s || '%')
            """, db);
        leaked.Parameters.AddWithValue("s", key.Secret);
        ((long)(await leaked.ExecuteScalarAsync())!).ShouldBe(0);

        host.Factory.Logs.Count.ShouldBeGreaterThan(0);
        host.Factory.Logs.Contains(key.Secret).ShouldBeFalse();
        host.Factory.Logs.Contains(key.Secret[^20..]).ShouldBeFalse();
    }

    [Fact]
    public async Task Missing_malformed_and_forged_keys_are_rejected()
    {
        var (owner, merchant) = await OwnerAsync();
        using var _ = owner;
        var key = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);

        using var none = MerchantScenario.ApiClient(host.Factory, null);
        var missing = await MerchantScenario.GetMerchantProfileAsync(none);
        missing.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        missing.Headers.WwwAuthenticate.ToString().ShouldStartWith("Bearer");
        (await MerchantScenario.ReadAsync(missing)).GetProperty("code").GetString().ShouldBe("api_key_required");

        var forged = key.Secret[..^4] + (key.Secret.EndsWith("0000", StringComparison.Ordinal) ? "1111" : "0000");
        foreach (var bad in new[] { "not-a-key", forged, key.Secret.ToUpperInvariant() })
        {
            using var client = MerchantScenario.ApiClient(host.Factory, bad);
            var response = await MerchantScenario.GetMerchantProfileAsync(client);
            response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
            (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe("invalid_api_key");
        }
    }

    [Fact]
    public async Task Revocation_takes_effect_immediately_despite_the_cache()
    {
        var (owner, merchant) = await OwnerAsync();
        using var _ = owner;
        var key = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);
        using var api = MerchantScenario.ApiClient(host.Factory, key.Secret);
        (await MerchantScenario.GetMerchantProfileAsync(api)).StatusCode.ShouldBe(HttpStatusCode.OK); // now cached in Redis

        var revoke = await owner.PostAsync($"/api/merchants/{merchant.Id}/api-keys/{key.Id}/revoke", null);

        revoke.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DashboardClient.ReadJsonAsync(revoke)).GetProperty("state").GetString().ShouldBe("Revoked");
        (await MerchantScenario.GetMerchantProfileAsync(api)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Rotation_keeps_the_old_key_for_the_grace_period_only()
    {
        var (owner, merchant) = await OwnerAsync();
        using var _ = owner;
        var original = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);

        var withGrace = await owner.PostAsync($"/api/merchants/{merchant.Id}/api-keys/{original.Id}/rotate", new { gracePeriodHours = 1 });
        withGrace.StatusCode.ShouldBe(HttpStatusCode.Created);
        var replacement = MerchantScenario.ReadKey(await DashboardClient.ReadJsonAsync(withGrace));

        using var oldApi = MerchantScenario.ApiClient(host.Factory, original.Secret);
        using var newApi = MerchantScenario.ApiClient(host.Factory, replacement.Secret);
        (await MerchantScenario.GetMerchantProfileAsync(oldApi)).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await MerchantScenario.GetMerchantProfileAsync(newApi)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await owner.PostAsync($"/api/merchants/{merchant.Id}/api-keys/{original.Id}/rotate", new { gracePeriodHours = 0 }))
            .StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity); // already rotated

        var immediate = await owner.PostAsync($"/api/merchants/{merchant.Id}/api-keys/{replacement.Id}/rotate", new { gracePeriodHours = 0 });
        immediate.StatusCode.ShouldBe(HttpStatusCode.Created);
        var third = MerchantScenario.ReadKey(await DashboardClient.ReadJsonAsync(immediate));
        using var thirdApi = MerchantScenario.ApiClient(host.Factory, third.Secret);

        (await MerchantScenario.GetMerchantProfileAsync(newApi)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await MerchantScenario.GetMerchantProfileAsync(thirdApi)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Closing_a_merchant_disables_its_keys()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        using var owner = await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword);
        var key = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);
        using var api = MerchantScenario.ApiClient(host.Factory, key.Secret);
        (await MerchantScenario.GetMerchantProfileAsync(api)).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await admin.PostAsync($"/api/merchants/{merchant.Id}/status", new { status = "Closed", reason = "Contract ended" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        (await MerchantScenario.GetMerchantProfileAsync(api)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Another_merchants_key_is_not_found_through_your_own_merchant()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var mine = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        var theirs = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        using var theirOwner = await MerchantScenario.LoginAsync(host, theirs.AdminEmail, theirs.AdminPassword);
        var theirKey = await MerchantScenario.CreateKeyAsync(theirOwner, theirs.Id);
        using var myOwner = await MerchantScenario.LoginAsync(host, mine.AdminEmail, mine.AdminPassword);

        var viaMine = await myOwner.PostAsync($"/api/merchants/{mine.Id}/api-keys/{theirKey.Id}/revoke", null);
        var viaTheirs = await myOwner.PostAsync($"/api/merchants/{theirs.Id}/api-keys/{theirKey.Id}/revoke", null);

        viaMine.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        viaTheirs.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        using var theirApi = MerchantScenario.ApiClient(host.Factory, theirKey.Secret);
        (await MerchantScenario.GetMerchantProfileAsync(theirApi)).StatusCode.ShouldBe(HttpStatusCode.OK);
    }

    [Fact]
    public async Task The_dashboard_session_cookie_is_not_accepted_by_the_merchant_api()
    {
        using var admin = await MerchantScenario.AdminAsync(host);

        var response = await admin.Http.GetAsync(new Uri("/v1/merchant", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Each_key_has_a_request_quota_shared_through_redis()
    {
        await using var factory = host.CreateFactory(new Dictionary<string, string?> { ["RateLimiting:MerchantApiPermitLimit"] = "3" });
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin, factory);
        using var owner = await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword, factory);
        var key = await MerchantScenario.CreateKeyAsync(owner, merchant.Id);
        using var api = MerchantScenario.ApiClient(factory, key.Secret);

        for (var i = 0; i < 3; i++)
        {
            (await MerchantScenario.GetMerchantProfileAsync(api)).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        var limited = await MerchantScenario.GetMerchantProfileAsync(api);
        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.RetryAfter.ShouldNotBeNull();
        (await MerchantScenario.ReadAsync(limited)).GetProperty("code").GetString().ShouldBe("rate_limited");

        // A second instance (configured with the default 10000 quota) sees the same counter, because it lives in Redis.
        using var otherInstance = MerchantScenario.ApiClient(host.Factory, key.Secret);
        (await MerchantScenario.GetMerchantProfileAsync(otherInstance)).Headers.GetValues("RateLimit-Remaining").Single().ShouldBe("9995"); // 10000 − 5 requests already counted
    }

    private async Task<(DashboardClient Owner, RegisteredMerchant Merchant)> OwnerAsync()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        return (await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword), merchant);
    }
}
