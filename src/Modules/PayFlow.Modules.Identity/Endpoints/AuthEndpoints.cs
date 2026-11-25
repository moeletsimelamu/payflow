using System.Security.Claims;
using FluentValidation;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Http;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Identity.Application;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Identity.Endpoints;

public sealed record LoginRequest(string Email, [property: Sensitive] string Password);

public sealed record MembershipResponse(string MerchantId, string Role);

public sealed record CurrentUserResponse(
    string Id,
    string Email,
    string DisplayName,
    bool IsPlatformAdmin,
    IReadOnlyList<MembershipResponse> Memberships,
    bool MfaEnabled);

public sealed record AcceptInvitationRequest(string Email, [property: Sensitive] string Token, [property: Sensitive] string Password);

internal sealed class LoginRequestValidator : AbstractValidator<LoginRequest>
{
    public LoginRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(r => r.Password).NotEmpty().MaximumLength(512);
    }
}

internal sealed class AcceptInvitationRequestValidator : AbstractValidator<AcceptInvitationRequest>
{
    public AcceptInvitationRequestValidator()
    {
        RuleFor(r => r.Email).NotEmpty().MaximumLength(256).EmailAddress();
        RuleFor(r => r.Token).NotEmpty().MaximumLength(2048);
        RuleFor(r => r.Password).NotEmpty().MaximumLength(512);
    }
}

internal static class AuthEndpoints
{
    private const string UserTarget = "user";

    // A valid hash of a random value, used to equalise response time for unknown e-mail addresses.
    private static readonly Lazy<string> DummyHash = new(() =>
        new PasswordHasher<PayFlowUser>().HashPassword(null!, Guid.NewGuid().ToString("N")));

    public static IEndpointRouteBuilder MapAuthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/auth").WithTags("Authentication");

        group.MapGet("/antiforgery", IssueAntiforgery).AllowAnonymous();

        group.MapPost("/login", Login)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication)
            .WithValidation<LoginRequest>();

        group.MapPost("/accept-invitation", AcceptInvitation)
            .AllowAnonymous()
            .RequireRateLimiting(RateLimitPolicies.Authentication)
            .WithValidation<AcceptInvitationRequest>();

        group.MapPost("/logout", Logout).RequireAuthorization(AuthPolicies.DashboardUser);

        group.MapGet("/me", Me).RequireAuthorization(AuthPolicies.DashboardUser);

        group.MapMfaEndpoints();

        return app;
    }

    private static IResult IssueAntiforgery(HttpContext http, IAntiforgery antiforgery)
    {
        AntiforgeryCookies.Issue(http, antiforgery);
        return Results.NoContent();
    }

    private static async Task<IResult> Login(
        LoginRequest request,
        HttpContext http,
        UserManager<PayFlowUser> users,
        SignInManager<PayFlowUser> signIn,
        IAuditLog audit,
        PayFlowDbContext db,
        IAntiforgery antiforgery,
        TimeProvider clock,
        MfaService mfa,
        Microsoft.Extensions.Options.IOptions<MfaOptions> mfaOptions)
    {
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null)
        {
            _ = users.PasswordHasher.VerifyHashedPassword(null!, DummyHash.Value, request.Password);
            audit.Record(new AuditEntry("identity.login.failed", UserTarget, null)
            {
                Actor = Actor.Anonymous,
                Changes = new { email = request.Email, reason = "unknown_user" },
            });
            await db.SaveChangesAsync(http.RequestAborted);
            return InvalidCredentials();
        }

        var publicId = PublicId.Create(IdPrefixes.User, user.Id);
        var actor = new Actor(ActorType.User, publicId, user.Email ?? publicId);
        var result = await signIn.CheckPasswordSignInAsync(user, request.Password, lockoutOnFailure: true);
        if (!result.Succeeded)
        {
            var action = result.IsLockedOut ? "identity.login.locked_out" : "identity.login.failed";
            var reason = result.IsLockedOut ? "locked_out" : result.IsNotAllowed ? "not_allowed" : "invalid_password";
            audit.Record(new AuditEntry(action, UserTarget, publicId) { Actor = actor, Changes = new { reason } });
            await db.SaveChangesAsync(http.RequestAborted);

            // Same response for unknown user, wrong password and lock-out, so accounts cannot be enumerated.
            return InvalidCredentials();
        }

        // A password alone is not enough for users with an authenticator, nor for platform admins
        // (who must set one up first): remember the verified password briefly and ask for the next step.
        var mfaEnabled = await mfa.IsEnabledAsync(user.Id, http.RequestAborted);
        if (mfaEnabled || (mfaOptions.Value.RequiredForPlatformAdmins && await users.IsInRoleAsync(user, PlatformRoles.PlatformAdmin)))
        {
            var next = mfaEnabled ? MfaSteps.Verify : MfaSteps.Enroll;
            await PendingSignIn.StartAsync(http, user.Id, next, mfaOptions.Value.PendingSignInLifetime, clock);
            audit.Record(new AuditEntry("identity.login.password_verified", UserTarget, publicId) { Actor = actor, Changes = new { next } });
            await db.SaveChangesAsync(http.RequestAborted);
            return Results.Json(new LoginStepResponse(next), statusCode: StatusCodes.Status202Accepted);
        }

        return Results.Ok(await SignInFlow.CompleteAsync(http, user, method: null, users, signIn, audit, db, antiforgery, clock));
    }

    /// <summary>Sets the first password of an invited user using their one-time invitation token.</summary>
    private static async Task<IResult> AcceptInvitation(
        AcceptInvitationRequest request,
        HttpContext http,
        UserManager<PayFlowUser> users,
        IAuditLog audit,
        PayFlowDbContext db)
    {
        var invalid = ErrorResults.Problem(Error.Validation("invalid_invitation", "The invitation is invalid or has expired."));
        var user = await users.FindByEmailAsync(request.Email);
        if (user is null || await users.HasPasswordAsync(user))
        {
            return invalid;
        }

        var result = await users.ResetPasswordAsync(user, request.Token, request.Password);
        if (!result.Succeeded)
        {
            if (result.Errors.Any(e => e.Code == nameof(IdentityErrorDescriber.InvalidToken)))
            {
                return invalid;
            }

            return ErrorResults.Problem(Error.Validation(
                "validation_failed",
                "The password does not meet the policy.",
                new Dictionary<string, string[]> { ["password"] = [.. result.Errors.Select(e => e.Description)] }));
        }

        var publicId = PublicId.Create(IdPrefixes.User, user.Id);
        audit.Record(new AuditEntry("identity.invitation.accepted", UserTarget, publicId)
        {
            Actor = new Actor(ActorType.User, publicId, user.Email ?? publicId),
        });
        await db.SaveChangesAsync(http.RequestAborted);
        return Results.NoContent();
    }

    private static async Task<IResult> Logout(
        HttpContext http,
        SignInManager<PayFlowUser> signIn,
        IAuditLog audit,
        PayFlowDbContext db)
    {
        audit.Record(new AuditEntry("identity.logout", UserTarget, http.User.FindFirstValue(PayFlowClaimTypes.PublicId)));
        await db.SaveChangesAsync(http.RequestAborted);
        await signIn.SignOutAsync();
        http.Response.Cookies.Delete(AuthCookies.XsrfToken, new CookieOptions { Secure = true, Path = "/" });
        return Results.NoContent();
    }

    private static IResult Me(HttpContext http) => Results.Ok(ToResponse(http.User));

    internal static CurrentUserResponse ToResponse(ClaimsPrincipal principal) => new(
        Id: principal.FindFirstValue(PayFlowClaimTypes.PublicId) ?? string.Empty,
        Email: principal.FindFirstValue(ClaimTypes.Email) ?? principal.FindFirstValue(ClaimTypes.Name) ?? string.Empty,
        DisplayName: principal.FindFirstValue(PayFlowClaimTypes.DisplayName) ?? string.Empty,
        IsPlatformAdmin: principal.IsInRole(PlatformRoles.PlatformAdmin),
        Memberships: CurrentUser.ParseMemberships(principal)
            .Select(m => new MembershipResponse(PublicId.Create(IdPrefixes.Merchant, m.MerchantId), m.Role))
            .ToList(),
        MfaEnabled: principal.HasClaim(PayFlowClaimTypes.MfaEnabled, "true"));

    private static IResult InvalidCredentials() =>
        ErrorResults.Problem(Error.Unauthorized("invalid_credentials", "The e-mail address or password is incorrect."));
}

/// <summary>Issues the JS-readable XSRF request token that Angular's HttpClient echoes back.</summary>
public static class AntiforgeryCookies
{
    public static void Issue(HttpContext http, IAntiforgery antiforgery)
    {
        ArgumentNullException.ThrowIfNull(http);
        ArgumentNullException.ThrowIfNull(antiforgery);
        var tokens = antiforgery.GetAndStoreTokens(http);
        http.Response.Cookies.Append(AuthCookies.XsrfToken, tokens.RequestToken!, new CookieOptions
        {
            HttpOnly = false,
            Secure = true,
            SameSite = SameSiteMode.Strict,
            Path = "/",
        });
    }
}
