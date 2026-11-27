using System.Net;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Auth;

/// <summary>SECURITY.md, SEC-4: breached and context-specific passwords are refused when a password is set.</summary>
public sealed class PasswordPolicyTests(PayFlowTestHost host)
{
    [Theory]
    [InlineData("Qwerty123456", "This password appears in lists of breached passwords")]
    [InlineData("payflow-is-great", "must not contain your email name, your display name or \"PayFlow\"")]
    public async Task An_invitation_cannot_be_accepted_with_a_weak_password_and_stays_usable(string password, string message)
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var email = $"operator-{Guid.NewGuid():N}@merchant.test";
        var invite = await m.Owner.PostAsync($"/api/merchants/{m.Id}/members", new { email, displayName = "Ops", role = "MerchantOperator" });
        invite.StatusCode.ShouldBe(HttpStatusCode.Created);
        var token = (await DashboardClient.ReadJsonAsync(invite)).GetProperty("invitationToken").GetString()!;

        using var anonymous = host.CreateDashboardClient();
        await anonymous.PrimeAntiforgeryAsync();
        var refused = await anonymous.PostAsync("/api/auth/accept-invitation", new { email, token, password });
        refused.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await DashboardClient.ReadJsonAsync(refused);
        problem.GetProperty("code").GetString().ShouldBe("validation_failed");
        problem.GetProperty("errors").GetProperty("password")[0].GetString()!.ShouldContain(message);

        // The token was not spent: a strong password still works with it.
        await MerchantScenario.AcceptInvitationAsync(host, email, token, "ops-strong-passphrase-42");
    }
}
