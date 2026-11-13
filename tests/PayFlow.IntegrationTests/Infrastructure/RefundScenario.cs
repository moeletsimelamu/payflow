using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Workers;

namespace PayFlow.IntegrationTests.Infrastructure;

public static class RefundScenario
{
    public static async Task<HttpResponseMessage> RefundAsync(HttpClient api, string paymentId, string? amount, string? key = null, string? reason = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/refunds")
        {
            Content = JsonContent.Create(new { amount, reason }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await api.SendAsync(request);
    }

    public static async Task<HttpResponseMessage> ReverseAsync(HttpClient api, string paymentId, string? key = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"/v1/payments/{paymentId}/reversal")
        {
            Content = JsonContent.Create(new { reason = "Customer cancelled" }),
        };
        request.Headers.Add("Idempotency-Key", key ?? Guid.NewGuid().ToString("N"));
        return await api.SendAsync(request);
    }

    public static async Task<JsonElement> GetPaymentAsync(HttpClient api, string paymentId) =>
        await MerchantScenario.ReadAsync(await api.GetAsync(new Uri($"/v1/payments/{paymentId}", UriKind.Relative)));

    public static async Task<string> PaidAsync(HttpClient api, string amount = "100.00", string method = "sim_card_success", string currency = "ZAR") =>
        (await PaymentScenario.CreateOkAsync(api, PaymentScenario.Body(amount, currency, method))).GetProperty("id").GetString()!;

    /// <summary>Runs one batch of a background job, as the timer would.</summary>
    public static async Task<int> RunJobAsync<TJob>(PayFlowApiFactory factory)
        where TJob : IRecurringJob
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TJob>().RunOnceAsync(CancellationToken.None);
    }

    /// <summary>Invites a user with <paramref name="role"/> to the merchant and signs them in.</summary>
    public static async Task<DashboardClient> MemberAsync(PayFlowTestHost host, DashboardClient merchantAdmin, string merchantId, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@merchant.test";
        var invite = await merchantAdmin.PostAsync($"/api/merchants/{merchantId}/members", new { email, displayName = role, role });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = (await DashboardClient.ReadJsonAsync(invite)).GetProperty("invitationToken").GetString()!;
        await MerchantScenario.AcceptInvitationAsync(host, email, token, "member-passphrase-123");
        return await MerchantScenario.LoginAsync(host, email, "member-passphrase-123");
    }
}
