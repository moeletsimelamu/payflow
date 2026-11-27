using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace PayFlow.Modules.Identity.Application;

/// <summary>
/// Dashboard session rules on top of the 30-minute sliding cookie:
/// <list type="bullet">
/// <item>Every minute the security stamp is checked and the claims (roles, merchant memberships) are
/// rebuilt from the database, so a removed member, a demoted admin or a changed password takes effect
/// within a minute in every open session (SecurityStampValidatorOptions.ValidationInterval).</item>
/// <item>However active it is, a session ends <see cref="Absolute"/> after sign-in and the user
/// authenticates again (ASVS 5.0, V7 Session Management: periodic re-authentication).</item>
/// </list>
/// </summary>
internal static class SessionLifetime
{
    public static readonly TimeSpan Absolute = TimeSpan.FromHours(12);

    /// <summary>Kept in the ticket's properties, which survive sliding renewals and claim refreshes.</summary>
    public const string AuthenticatedAtKey = ".payflow.authenticated_at";

    /// <summary>How the session's second factor was given (authenticator or recovery code); absent without one.</summary>
    public const string MfaKey = ".payflow.mfa";

    public static Task OnSigningIn(CookieSigningInContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        context.Properties.Items.TryAdd(AuthenticatedAtKey, now.ToString("O", CultureInfo.InvariantCulture));
        return Task.CompletedTask;
    }

    public static async Task OnValidatePrincipal(CookieValidatePrincipalContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var now = (context.Options.TimeProvider ?? TimeProvider.System).GetUtcNow();
        if (!context.Properties.Items.TryGetValue(AuthenticatedAtKey, out var raw) ||
            !DateTimeOffset.TryParseExact(raw, "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var authenticatedAt) ||
            now - authenticatedAt >= Absolute)
        {
            // Sessions issued before this rule existed carry no sign-in time: they end too.
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        // A platform admin session must have been opened with a second factor (sessions from before
        // MFA was required end here too).
        var mfa = context.HttpContext.RequestServices.GetRequiredService<IOptions<MfaOptions>>().Value;
        if (mfa.RequiredForPlatformAdmins &&
            context.Principal?.IsInRole(Contracts.PlatformRoles.PlatformAdmin) == true &&
            !context.Properties.Items.ContainsKey(MfaKey))
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            return;
        }

        await SecurityStampValidator.ValidatePrincipalAsync(context);
    }
}
