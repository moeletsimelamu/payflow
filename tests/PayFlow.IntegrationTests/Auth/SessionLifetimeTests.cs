using System.Net;
using Microsoft.Extensions.Time.Testing;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Auth;

/// <summary>
/// Sessions are cookies whose claims (roles, merchant memberships) authorise every request, so they
/// must follow changes made elsewhere: the security stamp is revalidated every minute and the
/// claims rebuilt from the database. Sessions also end after an absolute lifetime, however active.
/// </summary>
public sealed class SessionLifetimeTests(PayFlowTestHost host)
{
    [Fact]
    public async Task A_removed_member_loses_access_within_the_revalidation_interval_even_while_active()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(clock: clock);
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        var (member, userId) = await InviteMemberAsync(factory, m, "MerchantOperator");
        using var _ = member;

        (await GetMerchantAsync(member, m.Id)).StatusCode.ShouldBe(HttpStatusCode.OK);

        var removed = await m.Owner.SendAsync(HttpMethod.Delete, $"/api/merchants/{m.Id}/members/{userId}", null);
        removed.StatusCode.ShouldBe(HttpStatusCode.NoContent);

        clock.Advance(TimeSpan.FromMinutes(2)); // past the 1-minute revalidation, well inside the 30-minute session
        (await GetMerchantAsync(member, m.Id)).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task An_active_session_still_ends_after_the_absolute_lifetime()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(clock: clock);
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);

        // Active every 20 minutes, so the 30-minute sliding window never lapses...
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromHours(12) - TimeSpan.FromMinutes(20); elapsed += TimeSpan.FromMinutes(20))
        {
            clock.Advance(TimeSpan.FromMinutes(20));
            (await GetMerchantAsync(m.Owner, m.Id)).StatusCode.ShouldBe(HttpStatusCode.OK, $"after {clock.GetUtcNow() - clock.Start}");
        }

        // ...but 12 hours after signing in the user must authenticate again.
        clock.Advance(TimeSpan.FromMinutes(25));
        (await GetMerchantAsync(m.Owner, m.Id)).StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
    }

    private static Task<HttpResponseMessage> GetMerchantAsync(DashboardClient client, string merchantId) =>
        client.SendAsync(HttpMethod.Get, $"/api/merchants/{merchantId}", null, sendXsrf: false);

    private async Task<(DashboardClient Client, string UserId)> InviteMemberAsync(PayFlowApiFactory factory, PayingMerchant m, string role)
    {
        var email = $"{role.ToLowerInvariant()}-{Guid.NewGuid():N}@merchant.test";
        var invite = await m.Owner.PostAsync($"/api/merchants/{m.Id}/members", new { email, displayName = role, role });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await DashboardClient.ReadJsonAsync(invite);
        await MerchantScenario.AcceptInvitationAsync(host, email, body.GetProperty("invitationToken").GetString()!, "member-passphrase-123", factory);
        var client = await MerchantScenario.LoginAsync(host, email, "member-passphrase-123", factory);
        return (client, body.GetProperty("userId").GetString()!);
    }
}
