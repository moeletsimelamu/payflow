using System.Net;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Merchants;

public sealed class MerchantManagementTests(PayFlowTestHost host)
{
    [Fact]
    public async Task Platform_admin_onboards_a_merchant_and_its_first_admin()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);

        using var owner = await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword);
        var me = await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative)));
        var membership = me.GetProperty("memberships").EnumerateArray().Single();
        membership.GetProperty("merchantId").GetString().ShouldBe(merchant.Id);
        membership.GetProperty("role").GetString().ShouldBe("MerchantAdmin");

        var profile = await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{merchant.Id}", UriKind.Relative)));
        profile.GetProperty("status").GetString().ShouldBe("Active");
        profile.GetProperty("feePlanId").GetString()!.ShouldStartWith("fp_");
        var zar = profile.GetProperty("currencies").EnumerateArray().Single(c => c.GetProperty("currency").GetString() == "ZAR");
        zar.GetProperty("maxPaymentAmount").GetString().ShouldBe("50000.00");

        var activity = await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{merchant.Id}/activity", UriKind.Relative)));
        var actions = activity.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("action").GetString()).ToList();
        actions.ShouldContain("merchant.registered");
        actions.ShouldContain("merchant.member.invited");
        actions.ShouldContain("merchant.status_changed");
    }

    [Fact]
    public async Task Merchant_users_cannot_see_other_merchants_or_platform_endpoints()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var mine = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        var theirs = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        using var owner = await MerchantScenario.LoginAsync(host, mine.AdminEmail, mine.AdminPassword);

        (await owner.Http.GetAsync(new Uri($"/api/merchants/{theirs.Id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Http.GetAsync(new Uri($"/api/merchants/{theirs.Id}/api-keys", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.Http.GetAsync(new Uri("/api/merchants", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsync($"/api/merchants/{mine.Id}/status", new { status = "Closed", reason = "self-service" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Status_changes_follow_the_lifecycle()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);

        var noReason = await admin.PostAsync($"/api/merchants/{merchant.Id}/status", new { status = "Suspended", reason = (string?)null });
        noReason.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DashboardClient.ReadJsonAsync(noReason)).GetProperty("code").GetString().ShouldBe("reason_required");

        var backwards = await admin.PostAsync($"/api/merchants/{merchant.Id}/status", new { status = "PendingActivation", reason = "undo" });
        backwards.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DashboardClient.ReadJsonAsync(backwards)).GetProperty("code").GetString().ShouldBe("invalid_status_transition");

        var suspended = await admin.PostAsync($"/api/merchants/{merchant.Id}/status", new { status = "Suspended", reason = "Chargeback review" });
        (await DashboardClient.ReadJsonAsync(suspended)).GetProperty("status").GetString().ShouldBe("Suspended");
    }

    [Theory]
    [InlineData("ZAR", "10.001", "invalid_amount")]
    [InlineData("JPY", "100.5", "invalid_amount")]
    [InlineData("XYZ", "10.00", "unsupported_currency")]
    [InlineData("ZAR", "4.00", "max_below_minimum")]
    public async Task Currency_limits_are_validated_against_currency_rules(string currency, string max, string expectedCode)
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);

        var response = await SendJsonAsync(admin, HttpMethod.Put, $"/api/merchants/{merchant.Id}/currencies/{currency}", new { maxPaymentAmount = max, operatorRefundLimit = (string?)null });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DashboardClient.ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe(expectedCode);
    }

    [Fact]
    public async Task Fee_plans_are_versioned_and_immutable()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var name = $"Promo-{Guid.NewGuid():N}"[..20];

        var v1 = await admin.PostAsync("/api/fee-plans", new { name, percentageRate = "0.025", terms = new[] { new { currency = "ZAR", fixedFee = "1.50", minPaymentAmount = "5.00" } } });
        var v2 = await admin.PostAsync("/api/fee-plans", new { name, percentageRate = "0.022", terms = new[] { new { currency = "ZAR", fixedFee = "1.50", minPaymentAmount = "5.00" } } });

        v1.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await DashboardClient.ReadJsonAsync(v1)).GetProperty("version").GetInt32().ShouldBe(1);
        (await DashboardClient.ReadJsonAsync(v2)).GetProperty("version").GetInt32().ShouldBe(2);

        await using var db = await host.OpenOwnerConnectionAsync();
        await using var update = new NpgsqlCommand("UPDATE merchants.fee_plans SET percentage_rate = 0.5 WHERE name = @name", db);
        update.Parameters.AddWithValue("name", name);
        (await Should.ThrowAsync<PostgresException>(() => update.ExecuteNonQueryAsync())).MessageText.ShouldContain("append_only_violation");
    }

    [Fact]
    public async Task Invitation_tokens_work_once_and_the_last_admin_cannot_be_removed()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        using var owner = await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword);

        var operatorEmail = $"operator-{Guid.NewGuid():N}@merchant.test";
        var invite = await owner.PostAsync($"/api/merchants/{merchant.Id}/members", new { email = operatorEmail, displayName = "Ops", role = "MerchantOperator" });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = (await DashboardClient.ReadJsonAsync(invite)).GetProperty("invitationToken").GetString()!;

        await MerchantScenario.AcceptInvitationAsync(host, operatorEmail, token, "operator-passphrase-1");

        using var reuse = host.CreateDashboardClient();
        await reuse.PrimeAntiforgeryAsync();
        var second = await reuse.PostAsync("/api/auth/accept-invitation", new { email = operatorEmail, token, password = "attacker-passphrase-2" });
        second.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DashboardClient.ReadJsonAsync(second)).GetProperty("code").GetString().ShouldBe("invalid_invitation");

        using var operatorClient = await MerchantScenario.LoginAsync(host, operatorEmail, "operator-passphrase-1");
        (await operatorClient.Http.GetAsync(new Uri($"/api/merchants/{merchant.Id}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        (await operatorClient.Http.GetAsync(new Uri($"/api/merchants/{merchant.Id}/api-keys", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        var members = await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{merchant.Id}/members", UriKind.Relative)));
        var ownerId = members.EnumerateArray().Single(m => m.GetProperty("role").GetString() == "MerchantAdmin").GetProperty("userId").GetString();
        var removeSelf = await SendJsonAsync(owner, HttpMethod.Delete, $"/api/merchants/{merchant.Id}/members/{ownerId}", null);
        removeSelf.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DashboardClient.ReadJsonAsync(removeSelf)).GetProperty("code").GetString().ShouldBe("last_merchant_admin");
    }

    [Fact]
    public async Task Concurrent_profile_edits_do_not_lose_updates_silently()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var merchant = await MerchantScenario.CreateActiveMerchantAsync(host, admin);
        using var owner = await MerchantScenario.LoginAsync(host, merchant.AdminEmail, merchant.AdminPassword);

        var edits = Enumerable.Range(0, 8).Select(i =>
            SendJsonAsync(owner, HttpMethod.Patch, $"/api/merchants/{merchant.Id}", new { tradingName = $"Shop v{i}", contactEmail = "ops@merchant.test" }));
        var results = await Task.WhenAll(edits);

        results.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK || r.StatusCode == HttpStatusCode.Conflict);
        results.ShouldContain(r => r.StatusCode == HttpStatusCode.OK);
    }

    private static async Task<HttpResponseMessage> SendJsonAsync(DashboardClient client, HttpMethod method, string url, object? body)
    {
        if (method == HttpMethod.Post)
        {
            return await client.PostAsync(url, body);
        }

        return await client.SendAsync(method, url, body);
    }
}
