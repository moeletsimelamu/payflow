using FluentValidation;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PayFlow.Infrastructure.Http;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Application;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Identity.Endpoints;
using PayFlow.Modules.Identity.Infrastructure;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Identity;

public static class IdentityModule
{
    /// <summary>OWASP 2023 guidance for PBKDF2-HMAC-SHA512 (the ASP.NET Core Identity v3 hash).</summary>
    public const int PasswordHashIterations = 210_000;

    public static IModuleDbModel DbModel { get; } = new IdentityDbModel();

    public static IServiceCollection AddIdentityModule(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<IModuleDbModel, IdentityDbModel>();
        services.AddHttpContextAccessor();
        services.Configure<PlatformAdminSeedOptions>(configuration.GetSection(PlatformAdminSeedOptions.Section));
        services.Configure<MfaOptions>(configuration.GetSection(MfaOptions.Section));
        services.Configure<PasswordHasherOptions>(o => o.IterationCount = PasswordHashIterations);

        services
            .AddIdentityCore<PayFlowUser>(o =>
            {
                // NIST SP 800-63B: favour length over composition rules.
                o.Password.RequiredLength = 12;
                o.Password.RequiredUniqueChars = 4;
                o.Password.RequireDigit = false;
                o.Password.RequireLowercase = false;
                o.Password.RequireUppercase = false;
                o.Password.RequireNonAlphanumeric = false;
                o.Lockout.MaxFailedAccessAttempts = 5;
                o.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(15);
                o.Lockout.AllowedForNewUsers = true;
                o.User.RequireUniqueEmail = true;
                o.SignIn.RequireConfirmedEmail = false;
            })
            .AddRoles<PayFlowRole>()
            .AddEntityFrameworkStores<PayFlowDbContext>()
            .AddSignInManager()
            .AddClaimsPrincipalFactory<PayFlowClaimsPrincipalFactory>()
            .AddDefaultTokenProviders();

        services.Configure<SecurityStampValidatorOptions>(o => o.ValidationInterval = TimeSpan.FromMinutes(1));

        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.ConfigureApplicationCookie(o =>
        {
            o.Cookie.Name = AuthCookies.Session;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(30);
            o.SlidingExpiration = true;
            o.Events = new CookieAuthenticationEvents
            {
                // Assigning Events replaces the handlers AddIdentityCookies registered, including the
                // security-stamp check: SessionLifetime runs it again (with the absolute lifetime).
                OnSigningIn = SessionLifetime.OnSigningIn,
                OnValidatePrincipal = SessionLifetime.OnValidatePrincipal,

                // An API never redirects to a login page; it answers with problem details.
                OnRedirectToLogin = ctx => WriteProblem(ctx.HttpContext, Error.Unauthorized("authentication_required", "Sign in to continue.")),
                OnRedirectToAccessDenied = ctx => WriteProblem(ctx.HttpContext, Error.Forbidden("forbidden", "You do not have permission to perform this action.")),
            };
        });

        // The pending sign-in between a verified password and its second factor (Identity's
        // two-factor user-id scheme): short-lived, never renewed, same cookie hardening as the session.
        services.Configure<CookieAuthenticationOptions>(IdentityConstants.TwoFactorUserIdScheme, o =>
        {
            o.Cookie.Name = AuthCookies.PendingSignIn;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.ExpireTimeSpan = TimeSpan.FromMinutes(5);
            o.SlidingExpiration = false;
        });

        services.AddAntiforgery(o =>
        {
            o.HeaderName = AuthCookies.XsrfHeader;
            o.Cookie.Name = AuthCookies.Antiforgery;
            o.Cookie.HttpOnly = true;
            o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            o.Cookie.SameSite = SameSiteMode.Strict;
        });

        services.AddScoped<ICurrentUser, CurrentUser>();
        services.AddScoped<IAuthorizationHandler, MerchantAccessHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(AuthPolicies.DashboardUser, p => p
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireAuthenticatedUser())
            .AddPolicy(AuthPolicies.PlatformAdmin, p => p
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .RequireRole(PlatformRoles.PlatformAdmin))
            .AddPolicy(AuthPolicies.MerchantMember, p => p
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .AddRequirements(new MerchantAccessRequirement([.. MerchantRoles.All])))
            .AddPolicy(AuthPolicies.MerchantAdmin, p => p
                .AddAuthenticationSchemes(IdentityConstants.ApplicationScheme)
                .AddRequirements(new MerchantAccessRequirement(MerchantRoles.MerchantAdmin)));

        services.Configure<DataProtectionTokenProviderOptions>(o => o.TokenLifespan = TimeSpan.FromDays(3));
        services.AddScoped<IMerchantMembershipAdmin, MerchantMembershipAdmin>();
        services.AddScoped<IValidator<LoginRequest>, LoginRequestValidator>();
        services.AddScoped<IValidator<AcceptInvitationRequest>, AcceptInvitationRequestValidator>();
        services.AddScoped<IValidator<MfaVerifyRequest>, MfaVerifyRequestValidator>();
        services.AddScoped<IValidator<MfaCodeRequest>, MfaCodeRequestValidator>();
        services.AddScoped<MfaService>();
        services.AddScoped<IdentitySeeder>();
        return services;
    }

    public static IEndpointRouteBuilder MapIdentityEndpoints(this IEndpointRouteBuilder app) => app.MapAuthEndpoints();

    private static async Task WriteProblem(HttpContext http, Error error)
    {
        await ErrorResults.Problem(error).ExecuteAsync(http);
    }
}
