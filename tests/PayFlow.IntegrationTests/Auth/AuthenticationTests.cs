using System.Net;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Identity.Contracts;

namespace PayFlow.IntegrationTests.Auth;

public sealed class AuthenticationTests(PayFlowTestHost host)
{
    [Fact]
    public async Task Profile_requires_authentication()
    {
        using var client = host.CreateDashboardClient();

        var response = await client.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var problem = await DashboardClient.ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("authentication_required");
        problem.GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public async Task Login_without_an_xsrf_token_is_rejected()
    {
        using var client = host.CreateDashboardClient();

        var response = await client.PostAsync("/api/auth/login",
            new { email = PayFlowTestHost.AdminEmail, password = PayFlowTestHost.AdminPassword }, sendXsrf: false);

        response.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DashboardClient.ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("csrf_token_invalid");
    }

    [Fact]
    public async Task Admin_logs_in_with_a_hardened_session_cookie()
    {
        using var client = host.CreateDashboardClient();

        var response = await client.LoginAsAdminAsync();

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var sessionCookie = response.Headers.GetValues("Set-Cookie")
            .Single(c => c.StartsWith(AuthCookies.Session + "=", StringComparison.Ordinal))
            .ToLowerInvariant();
        sessionCookie.ShouldContain("httponly");
        sessionCookie.ShouldContain("secure");
        sessionCookie.ShouldContain("samesite=strict");

        var body = await DashboardClient.ReadJsonAsync(response);
        body.GetProperty("isPlatformAdmin").GetBoolean().ShouldBeTrue();
        body.GetProperty("id").GetString()!.ShouldStartWith("usr_");

        var me = await client.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative));
        me.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await DashboardClient.ReadJsonAsync(me)).GetProperty("email").GetString().ShouldBe(PayFlowTestHost.AdminEmail);
    }

    [Fact]
    public async Task Wrong_password_and_unknown_user_get_identical_responses()
    {
        // Never fail logins against the shared admin: five failures would lock it for every other test.
        var email = $"wrong-pw-{Guid.NewGuid():N}@payflow.test";
        await host.CreateUserAsync(email, "the-real-passphrase-123");
        using var client = host.CreateDashboardClient();

        var wrongPassword = await client.LoginAsync(email, "not-the-right-passphrase");
        var unknownUser = await client.LoginAsync($"nobody-{Guid.NewGuid():N}@payflow.test", "not-the-right-passphrase");

        wrongPassword.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        unknownUser.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        var a = await DashboardClient.ReadJsonAsync(wrongPassword);
        var b = await DashboardClient.ReadJsonAsync(unknownUser);
        a.GetProperty("code").GetString().ShouldBe("invalid_credentials");
        b.GetProperty("code").GetString().ShouldBe("invalid_credentials");
        a.GetProperty("detail").GetString().ShouldBe(b.GetProperty("detail").GetString());
    }

    [Fact]
    public async Task Five_failures_lock_the_account_even_for_the_correct_password()
    {
        var email = $"lockout-{Guid.NewGuid():N}@payflow.test";
        const string password = "lockout-test-passphrase";
        await host.CreateUserAsync(email, password);
        using var client = host.CreateDashboardClient();

        for (var i = 0; i < 5; i++)
        {
            (await client.LoginAsync(email, "wrong-passphrase-attempt")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var correct = await client.LoginAsync(email, password);

        correct.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        (await DashboardClient.ReadJsonAsync(correct)).GetProperty("code").GetString().ShouldBe("invalid_credentials");

        await using var db = await host.OpenOwnerConnectionAsync();
        await using var lockout = new NpgsqlCommand("SELECT lockout_end > now() FROM identity.users WHERE email = @email", db);
        lockout.Parameters.AddWithValue("email", email);
        ((bool)(await lockout.ExecuteScalarAsync())!).ShouldBeTrue();

        await using var audit = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE action = 'identity.login.locked_out' AND actor_display = @email", db);
        audit.Parameters.AddWithValue("email", email);
        ((long)(await audit.ExecuteScalarAsync())!).ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Logout_ends_the_session()
    {
        using var client = host.CreateDashboardClient();
        (await client.LoginAsAdminAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);

        var logout = await client.PostAsync("/api/auth/logout", body: null);

        logout.StatusCode.ShouldBe(HttpStatusCode.NoContent);
        (await client.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Invalid_login_payload_returns_field_errors()
    {
        using var client = host.CreateDashboardClient();

        var response = await client.LoginAsync(string.Empty, string.Empty);

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await DashboardClient.ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("validation_failed");
        problem.GetProperty("errors").TryGetProperty("email", out _).ShouldBeTrue();
        problem.GetProperty("errors").TryGetProperty("password", out _).ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_json_properties_are_rejected()
    {
        using var client = host.CreateDashboardClient();
        await client.PrimeAntiforgeryAsync();

        var response = await client.PostAsync("/api/auth/login",
            new { email = PayFlowTestHost.AdminEmail, password = PayFlowTestHost.AdminPassword, rememberMe = true });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Successful_login_is_audited_with_the_request_correlation_id()
    {
        using var client = host.CreateDashboardClient();
        await client.PrimeAntiforgeryAsync();
        var correlationId = $"itest-{Guid.NewGuid():N}";

        var response = await client.LoginAsync(PayFlowTestHost.AdminEmail, PayFlowTestHost.AdminPassword, correlationId);

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        var userId = (await DashboardClient.ReadJsonAsync(response)).GetProperty("id").GetString();
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var command = new NpgsqlCommand(
            "SELECT actor_type, actor_id FROM audit.audit_events WHERE action = 'identity.login.succeeded' AND correlation_id = @id", db);
        command.Parameters.AddWithValue("id", correlationId);
        await using var reader = await command.ExecuteReaderAsync();
        (await reader.ReadAsync()).ShouldBeTrue("expected an audit row for the login");
        reader.GetString(0).ShouldBe("User");
        reader.GetString(1).ShouldBe(userId);
        (await reader.ReadAsync()).ShouldBeFalse("expected exactly one audit row");
    }

    [Fact]
    public async Task Simultaneous_logins_of_the_same_user_are_all_audited()
    {
        // Regression: concurrent logins used to collide on Identity's concurrency stamp and drop audit rows.
        var email = $"busy-{Guid.NewGuid():N}@payflow.test";
        const string password = "concurrent-login-passphrase";
        await host.CreateUserAsync(email, password);
        var correlation = $"itest-burst-{Guid.NewGuid():N}"[..40];
        var clients = Enumerable.Range(0, 10).Select(_ => host.CreateDashboardClient()).ToList();
        foreach (var c in clients)
        {
            await c.PrimeAntiforgeryAsync();
        }

        var responses = await Task.WhenAll(clients.Select((c, i) => c.LoginAsync(email, password, $"{correlation}-{i}")));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.OK);
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var count = new NpgsqlCommand(
            "SELECT count(*) FROM audit.audit_events WHERE action = 'identity.login.succeeded' AND correlation_id LIKE @p", db);
        count.Parameters.AddWithValue("p", correlation + "-%");
        ((long)(await count.ExecuteScalarAsync())!).ShouldBe(10);
        clients.ForEach(c => c.Dispose());
    }

    [Fact]
    public async Task Login_is_rate_limited_per_client()
    {
        await using var factory = host.CreateFactory(new Dictionary<string, string?>
        {
            ["RateLimiting:AuthenticationPermitLimit"] = "3",
        });
        using var client = host.CreateDashboardClient(factory);

        for (var i = 0; i < 3; i++)
        {
            (await client.LoginAsync($"nobody-{Guid.NewGuid():N}@payflow.test", "wrong-passphrase-attempt")).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        }

        var limited = await client.LoginAsync(PayFlowTestHost.AdminEmail, PayFlowTestHost.AdminPassword);

        limited.StatusCode.ShouldBe(HttpStatusCode.TooManyRequests);
        limited.Headers.Contains("Retry-After").ShouldBeTrue();
        (await DashboardClient.ReadJsonAsync(limited)).GetProperty("code").GetString().ShouldBe("rate_limited");
    }
}
