using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Http;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Identity.Application;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Identity.Endpoints;

/// <summary>What a password-verified sign-in must do next: <c>mfa</c> (enter a code) or <c>mfa_enrollment</c>.</summary>
public sealed record LoginStepResponse(string Next);

public sealed record MfaVerifyRequest([property: Sensitive] string? Code, [property: Sensitive] string? RecoveryCode);

public sealed record MfaCodeRequest([property: Sensitive] string Code);

public sealed record MfaEnrollmentResponse([property: Sensitive] string Secret, [property: Sensitive] string ProvisioningUri);

public sealed record MfaRecoveryCodesResponse([property: Sensitive] IReadOnlyList<string> RecoveryCodes, string Notice, CurrentUserResponse? User = null);

public sealed record MfaStatusResponse(bool Enabled, bool Required, int RecoveryCodesRemaining);

internal sealed class MfaVerifyRequestValidator : AbstractValidator<MfaVerifyRequest>
{
    public MfaVerifyRequestValidator()
    {
        RuleFor(r => r.Code).MaximumLength(20);
        RuleFor(r => r.RecoveryCode).MaximumLength(64);
        RuleFor(r => r).Must(r => !string.IsNullOrWhiteSpace(r.Code) || !string.IsNullOrWhiteSpace(r.RecoveryCode))
            .WithName("code").WithMessage("Enter the code from your authenticator app or a recovery code.");
    }
}

internal sealed class MfaCodeRequestValidator : AbstractValidator<MfaCodeRequest>
{
    public MfaCodeRequestValidator() => RuleFor(r => r.Code).NotEmpty().MaximumLength(20);
}

/// <summary>Constants for the sign-in step between a verified password and a session.</summary>
public static class MfaSteps
{
    public const string Verify = "mfa";
    public const string Enroll = "mfa_enrollment";
}

/// <summary>
/// The short-lived cookie (Identity's two-factor user-id scheme) that remembers a verified password
/// while the user enters a code or enrols an authenticator. It grants nothing else.
/// </summary>
internal static class PendingSignIn
{
    private const string StepClaim = "pf:mfa_step";

    public static Task StartAsync(HttpContext http, Guid userId, string step, TimeSpan lifetime, TimeProvider clock)
    {
        var identity = new ClaimsIdentity(IdentityConstants.TwoFactorUserIdScheme);
        identity.AddClaim(new Claim(ClaimTypes.Name, userId.ToString()));
        identity.AddClaim(new Claim(StepClaim, step));
        var now = clock.GetUtcNow();
        return http.SignInAsync(IdentityConstants.TwoFactorUserIdScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IssuedUtc = now, ExpiresUtc = now + lifetime, IsPersistent = false, AllowRefresh = false });
    }

    public static async Task<(Guid UserId, string Step)?> ReadAsync(HttpContext http)
    {
        var result = await http.AuthenticateAsync(IdentityConstants.TwoFactorUserIdScheme);
        if (!result.Succeeded ||
            !Guid.TryParse(result.Principal.FindFirstValue(ClaimTypes.Name), out var userId) ||
            result.Principal.FindFirstValue(StepClaim) is not { } step)
        {
            return null;
        }

        return (userId, step);
    }

    public static Task EndAsync(HttpContext http) => http.SignOutAsync(IdentityConstants.TwoFactorUserIdScheme);
}

internal static class MfaEndpoints
{
    private const string UserTarget = "user";
    private const string RecoveryNotice = "Store these recovery codes somewhere safe. Each works once, and PayFlow shows them only now.";

    private static readonly Error CodeInvalid = Error.Unauthorized("mfa_code_invalid", "The code is incorrect or has already been used. After 5 failed attempts, wait 15 minutes.");
    private static readonly Error SignInExpired = Error.Unauthorized("mfa_sign_in_expired", "Sign in again to continue.");

    public static RouteGroupBuilder MapMfaEndpoints(this RouteGroupBuilder group)
    {
        group.MapPost("/mfa/verify", Verify).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication).WithValidation<MfaVerifyRequest>();
        group.MapPost("/mfa/enrollment", StartEnrollment).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication);
        group.MapPost("/mfa/enrollment/confirm", ConfirmEnrollment).AllowAnonymous().RequireRateLimiting(RateLimitPolicies.Authentication).WithValidation<MfaCodeRequest>();
        group.MapGet("/mfa", Status).RequireAuthorization(AuthPolicies.DashboardUser);
        group.MapPost("/mfa/recovery-codes", RegenerateRecoveryCodes).RequireAuthorization(AuthPolicies.DashboardUser).WithValidation<MfaCodeRequest>();
        group.MapPost("/mfa/disable", Disable).RequireAuthorization(AuthPolicies.DashboardUser).WithValidation<MfaCodeRequest>();
        return group;
    }

    /// <summary>Second step of sign-in: an authenticator or recovery code after a verified password.</summary>
    private static async Task<IResult> Verify(
        MfaVerifyRequest request, HttpContext http, UserManager<PayFlowUser> users, SignInManager<PayFlowUser> signIn,
        MfaService mfa, IAuditLog audit, PayFlowDbContext db, IAntiforgery antiforgery, TimeProvider clock)
    {
        if (await PendingSignIn.ReadAsync(http) is not { Step: MfaSteps.Verify } pending ||
            await users.FindByIdAsync(pending.UserId.ToString()) is not { } user)
        {
            return ErrorResults.Problem(SignInExpired);
        }

        var (actor, publicId) = ActorFor(user);
        if (await users.IsLockedOutAsync(user))
        {
            return await FailAsync(http, users, user, actor, publicId, audit, db, "locked_out");
        }

        var check = await mfa.VerifyAsync(user.Id, request.Code, request.RecoveryCode, http.RequestAborted);
        if (check == MfaCheck.Failed)
        {
            return await FailAsync(http, users, user, actor, publicId, audit, db, "invalid_code");
        }

        await users.ResetAccessFailedCountAsync(user);
        await PendingSignIn.EndAsync(http);
        var method = check == MfaCheck.RecoveryCode ? "recovery_code" : "authenticator";
        if (check == MfaCheck.RecoveryCode)
        {
            audit.Record(new AuditEntry("identity.mfa.recovery_code_used", UserTarget, publicId)
            {
                Actor = actor,
                Changes = new { remaining = await mfa.RemainingRecoveryCodesAsync(user.Id, http.RequestAborted) },
            });
        }

        return Results.Ok(await SignInFlow.CompleteAsync(http, user, method, users, signIn, audit, db, antiforgery, clock));
    }

    /// <summary>Creates a new authenticator secret, for a pending sign-in that must enrol or a signed-in user.</summary>
    private static async Task<IResult> StartEnrollment(HttpContext http, UserManager<PayFlowUser> users, MfaService mfa)
    {
        if (await EnrollingUserAsync(http, users) is not { } enrolling)
        {
            return ErrorResults.Problem(SignInExpired);
        }

        if (await mfa.IsEnabledAsync(enrolling.User.Id, http.RequestAborted))
        {
            return ErrorResults.Problem(Error.Conflict("mfa_already_enabled", "An authenticator is already set up. Disable it first to replace it."));
        }

        var enrollment = await mfa.StartEnrollmentAsync(enrolling.User.Id, enrolling.User.Email ?? enrolling.User.Id.ToString(), http.RequestAborted);
        return Results.Ok(new MfaEnrollmentResponse(enrollment.Secret, enrollment.ProvisioningUri));
    }

    /// <summary>Activates the authenticator with its first code and returns the recovery codes, once.</summary>
    private static async Task<IResult> ConfirmEnrollment(
        MfaCodeRequest request, HttpContext http, UserManager<PayFlowUser> users, SignInManager<PayFlowUser> signIn,
        MfaService mfa, IAuditLog audit, PayFlowDbContext db, IAntiforgery antiforgery, TimeProvider clock)
    {
        if (await EnrollingUserAsync(http, users) is not { } enrolling)
        {
            return ErrorResults.Problem(SignInExpired);
        }

        var user = enrolling.User;
        var (actor, publicId) = ActorFor(user);
        if (await users.IsLockedOutAsync(user))
        {
            return await FailAsync(http, users, user, actor, publicId, audit, db, "locked_out");
        }

        if (await mfa.ConfirmEnrollmentAsync(user.Id, request.Code, http.RequestAborted) is not { } codes)
        {
            return await FailAsync(http, users, user, actor, publicId, audit, db, "invalid_code");
        }

        // Marks the account as two-factor so a correct password alone no longer resets the failure
        // count, and rotates the security stamp so every other session must sign in again.
        await users.SetTwoFactorEnabledAsync(user, true);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateSecurityStampAsync(user);
        audit.Record(new AuditEntry("identity.mfa.enabled", UserTarget, publicId) { Actor = actor });

        CurrentUserResponse signedIn;
        if (enrolling.Pending)
        {
            await PendingSignIn.EndAsync(http);
            signedIn = await SignInFlow.CompleteAsync(http, user, "authenticator", users, signIn, audit, db, antiforgery, clock);
        }
        else
        {
            await db.SaveChangesAsync(http.RequestAborted);
            signedIn = await SignInFlow.RenewAsync(http, user, "authenticator", signIn, antiforgery);
        }

        return Results.Ok(new MfaRecoveryCodesResponse(codes, RecoveryNotice, signedIn));
    }

    private static async Task<IResult> Status(HttpContext http, UserManager<PayFlowUser> users, MfaService mfa, IOptions<MfaOptions> options)
    {
        var user = await users.GetUserAsync(http.User);
        if (user is null)
        {
            return ErrorResults.Problem(SignInExpired);
        }

        var enabled = await mfa.IsEnabledAsync(user.Id, http.RequestAborted);
        var required = options.Value.RequiredForPlatformAdmins && await users.IsInRoleAsync(user, PlatformRoles.PlatformAdmin);
        return Results.Ok(new MfaStatusResponse(enabled, required, enabled ? await mfa.RemainingRecoveryCodesAsync(user.Id, http.RequestAborted) : 0));
    }

    private static async Task<IResult> RegenerateRecoveryCodes(
        MfaCodeRequest request, HttpContext http, UserManager<PayFlowUser> users, MfaService mfa, IAuditLog audit, PayFlowDbContext db)
    {
        var user = await users.GetUserAsync(http.User);
        if (user is null)
        {
            return ErrorResults.Problem(SignInExpired);
        }

        var (actor, publicId) = ActorFor(user);
        if (await users.IsLockedOutAsync(user) || await mfa.VerifyAsync(user.Id, request.Code, null, http.RequestAborted) != MfaCheck.Authenticator)
        {
            return await FailAsync(http, users, user, actor, publicId, audit, db, "invalid_code");
        }

        await users.ResetAccessFailedCountAsync(user);
        var codes = await mfa.RegenerateRecoveryCodesAsync(user.Id, http.RequestAborted);
        audit.Record(new AuditEntry("identity.mfa.recovery_codes_regenerated", UserTarget, publicId) { Actor = actor });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.Ok(new MfaRecoveryCodesResponse(codes, RecoveryNotice));
    }

    private static async Task<IResult> Disable(
        MfaCodeRequest request, HttpContext http, UserManager<PayFlowUser> users, SignInManager<PayFlowUser> signIn,
        MfaService mfa, IAuditLog audit, PayFlowDbContext db, IAntiforgery antiforgery, IOptions<MfaOptions> options)
    {
        var user = await users.GetUserAsync(http.User);
        if (user is null)
        {
            return ErrorResults.Problem(SignInExpired);
        }

        if (options.Value.RequiredForPlatformAdmins && await users.IsInRoleAsync(user, PlatformRoles.PlatformAdmin))
        {
            return ErrorResults.Problem(Error.Forbidden("mfa_required", "Platform admins must keep an authenticator. Set up a new one by using a recovery code first."));
        }

        var (actor, publicId) = ActorFor(user);
        if (await users.IsLockedOutAsync(user) || await mfa.VerifyAsync(user.Id, request.Code, null, http.RequestAborted) != MfaCheck.Authenticator)
        {
            return await FailAsync(http, users, user, actor, publicId, audit, db, "invalid_code");
        }

        await mfa.DisableAsync(user.Id, http.RequestAborted);
        await users.SetTwoFactorEnabledAsync(user, false);
        await users.ResetAccessFailedCountAsync(user);
        await users.UpdateSecurityStampAsync(user);
        audit.Record(new AuditEntry("identity.mfa.disabled", UserTarget, publicId) { Actor = actor });
        await db.SaveChangesAsync(http.RequestAborted);
        await SignInFlow.RenewAsync(http, user, method: null, signIn, antiforgery);
        return Results.NoContent();
    }

    /// <summary>A pending sign-in that must enrol, or a signed-in user setting up an authenticator voluntarily.</summary>
    private static async Task<(PayFlowUser User, bool Pending)?> EnrollingUserAsync(HttpContext http, UserManager<PayFlowUser> users)
    {
        if (await PendingSignIn.ReadAsync(http) is { Step: MfaSteps.Enroll } pending)
        {
            return await users.FindByIdAsync(pending.UserId.ToString()) is { } user ? (user, true) : null;
        }

        return http.User.Identity?.IsAuthenticated == true && await users.GetUserAsync(http.User) is { } signedIn ? (signedIn, false) : null;
    }

    /// <summary>Counts the failure towards the account lock-out, audits it, and answers with one generic error.</summary>
    private static async Task<IResult> FailAsync(
        HttpContext http, UserManager<PayFlowUser> users, PayFlowUser user, Actor actor, string publicId, IAuditLog audit, PayFlowDbContext db, string reason)
    {
        if (reason != "locked_out")
        {
            await users.AccessFailedAsync(user);
        }

        audit.Record(new AuditEntry("identity.mfa.failed", UserTarget, publicId) { Actor = actor, Changes = new { reason } });
        await db.SaveChangesAsync(http.RequestAborted);
        return ErrorResults.Problem(CodeInvalid);
    }

    private static (Actor Actor, string PublicId) ActorFor(PayFlowUser user)
    {
        var publicId = PublicId.Create(IdPrefixes.User, user.Id);
        return (new Actor(ActorType.User, publicId, user.Email ?? publicId), publicId);
    }
}

/// <summary>Turns a verified user into a dashboard session.</summary>
internal static class SignInFlow
{
    public static async Task<CurrentUserResponse> CompleteAsync(
        HttpContext http, PayFlowUser user, string? method, UserManager<PayFlowUser> users, SignInManager<PayFlowUser> signIn,
        IAuditLog audit, PayFlowDbContext db, IAntiforgery antiforgery, TimeProvider clock)
    {
        var publicId = PublicId.Create(IdPrefixes.User, user.Id);
        var actor = new Actor(ActorType.User, publicId, user.Email ?? publicId);

        // Not via UserManager.UpdateAsync: its concurrency stamp makes simultaneous logins of the same
        // user fail (silently, as an IdentityResult), which used to lose the audit record. A direct
        // update has no stamp to conflict on, and the audit record is committed on its own.
        var now = clock.GetUtcNow();
        await db.Users.Where(u => u.Id == user.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.LastLoginAt, now), http.RequestAborted);
        audit.Record(new AuditEntry("identity.login.succeeded", "user", publicId)
        {
            Actor = actor,
            Changes = method is null ? null : new { mfa = method },
        });
        await db.SaveChangesAsync(http.RequestAborted);

        var properties = new AuthenticationProperties();
        if (method is not null)
        {
            properties.Items[SessionLifetime.MfaKey] = method;
        }

        await signIn.SignInAsync(user, properties);
        return await IssueAntiforgeryAsync(http, user, signIn, antiforgery);
    }

    /// <summary>
    /// Re-issues the current session after the user changed their own authenticator (the security
    /// stamp rotated), keeping its original sign-in time so the absolute lifetime does not restart.
    /// </summary>
    public static async Task<CurrentUserResponse> RenewAsync(HttpContext http, PayFlowUser user, string? method, SignInManager<PayFlowUser> signIn, IAntiforgery antiforgery)
    {
        var current = await http.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var properties = new AuthenticationProperties();
        if (current.Properties?.Items.TryGetValue(SessionLifetime.AuthenticatedAtKey, out var authenticatedAt) == true)
        {
            properties.Items[SessionLifetime.AuthenticatedAtKey] = authenticatedAt;
        }

        if (method is not null)
        {
            properties.Items[SessionLifetime.MfaKey] = method;
        }

        await signIn.SignInAsync(user, properties);
        return await IssueAntiforgeryAsync(http, user, signIn, antiforgery);
    }

    private static async Task<CurrentUserResponse> IssueAntiforgeryAsync(HttpContext http, PayFlowUser user, SignInManager<PayFlowUser> signIn, IAntiforgery antiforgery)
    {
        // The antiforgery token is bound to the user, so issue a fresh one for the new principal.
        var principal = await signIn.CreateUserPrincipalAsync(user);
        http.User = principal;
        AntiforgeryCookies.Issue(http, antiforgery);
        return AuthEndpoints.ToResponse(principal);
    }
}
