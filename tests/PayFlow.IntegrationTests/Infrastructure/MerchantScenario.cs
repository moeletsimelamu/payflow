using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace PayFlow.IntegrationTests.Infrastructure;

public sealed record RegisteredMerchant(string Id, string AdminEmail, string AdminPassword);

public sealed record IssuedKey(string Id, string Prefix, string Secret);

/// <summary>Builds merchants, users and API keys through the public HTTP API, as a reviewer would.</summary>
public static class MerchantScenario
{
    public static async Task<DashboardClient> AdminAsync(PayFlowTestHost host, PayFlowApiFactory? factory = null)
    {
        var admin = host.CreateDashboardClient(factory);
        (await admin.LoginAsAdminAsync()).StatusCode.ShouldBe(HttpStatusCode.OK);
        return admin;
    }

    /// <summary>Registers and activates a ZAR/USD merchant, and onboards its first MerchantAdmin.</summary>
    public static async Task<RegisteredMerchant> CreateActiveMerchantAsync(PayFlowTestHost host, DashboardClient admin, PayFlowApiFactory? factory = null)
    {
        var tag = Guid.NewGuid().ToString("N")[..10];
        var adminEmail = $"owner-{tag}@merchant.test";
        var response = await admin.PostAsync("/api/merchants", new
        {
            legalName = $"Test Merchant {tag} (Pty) Ltd",
            tradingName = $"Shop {tag}",
            contactEmail = $"ops-{tag}@merchant.test",
            countryCode = "ZA",
            currencies = new[]
            {
                new { currency = "ZAR", maxPaymentAmount = "50000.00", operatorRefundLimit = (string?)"1000.00" },
                new { currency = "USD", maxPaymentAmount = "5000.00", operatorRefundLimit = (string?)null },
            },
            adminEmail,
            adminDisplayName = "Merchant Owner",
        });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await DashboardClient.ReadJsonAsync(response);
        var id = body.GetProperty("merchant").GetProperty("id").GetString()!;
        var token = body.GetProperty("adminInvitation").GetProperty("invitationToken").GetString()!;

        (await admin.PostAsync($"/api/merchants/{id}/status", new { status = "Active", reason = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.OK);

        const string password = "merchant-owner-passphrase";
        await AcceptInvitationAsync(host, adminEmail, token, password, factory);
        return new RegisteredMerchant(id, adminEmail, password);
    }

    public static async Task AcceptInvitationAsync(PayFlowTestHost host, string email, string token, string password, PayFlowApiFactory? factory = null)
    {
        using var anonymous = host.CreateDashboardClient(factory);
        await anonymous.PrimeAntiforgeryAsync();
        var accepted = await anonymous.PostAsync("/api/auth/accept-invitation", new { email, token, password });
        accepted.StatusCode.ShouldBe(HttpStatusCode.NoContent, await accepted.Content.ReadAsStringAsync());
    }

    public static async Task<DashboardClient> LoginAsync(PayFlowTestHost host, string email, string password, PayFlowApiFactory? factory = null)
    {
        var client = host.CreateDashboardClient(factory);
        var response = await client.LoginAsync(email, password);
        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        return client;
    }

    public static async Task<IssuedKey> CreateKeyAsync(DashboardClient merchantAdmin, string merchantId, string name = "Checkout server")
    {
        var response = await merchantAdmin.PostAsync($"/api/merchants/{merchantId}/api-keys", new { name });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return ReadKey(await DashboardClient.ReadJsonAsync(response));
    }

    public static IssuedKey ReadKey(JsonElement body) => new(
        body.GetProperty("key").GetProperty("id").GetString()!,
        body.GetProperty("key").GetProperty("prefix").GetString()!,
        body.GetProperty("secret").GetString()!);

    /// <summary>A merchant's server calling the <c>/v1</c> API with a bearer key.</summary>
    public static HttpClient ApiClient(PayFlowApiFactory factory, string? secret)
    {
        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://localhost"), HandleCookies = false });
        if (secret is not null)
        {
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secret);
        }

        return client;
    }

    public static Task<HttpResponseMessage> GetMerchantProfileAsync(HttpClient api) => api.GetAsync(new Uri("/v1/merchant", UriKind.Relative));

    public static async Task<JsonElement> ReadAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.Clone();

}
