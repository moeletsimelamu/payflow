namespace PayFlow.Modules.Identity.Contracts;

/// <summary>Global (platform-wide) role.</summary>
public static class PlatformRoles
{
    public const string PlatformAdmin = "PlatformAdmin";
}

/// <summary>Roles a user can hold within a specific merchant (via a membership).</summary>
public static class MerchantRoles
{
    public const string MerchantAdmin = "MerchantAdmin";
    public const string MerchantOperator = "MerchantOperator";

    public static IReadOnlyList<string> All { get; } = [MerchantAdmin, MerchantOperator];
}

/// <summary>Authorization policy names used by endpoints across modules.</summary>
public static class AuthPolicies
{
    /// <summary>Signed-in dashboard user.</summary>
    public const string DashboardUser = "DashboardUser";

    public const string PlatformAdmin = "PlatformAdmin";

    /// <summary>Any membership of the merchant in the <c>{merchantId}</c> route value (or PlatformAdmin).</summary>
    public const string MerchantMember = "MerchantMember";

    /// <summary>MerchantAdmin membership of the merchant in the <c>{merchantId}</c> route value (or PlatformAdmin).</summary>
    public const string MerchantAdmin = "MerchantAdmin";
}

public static class RateLimitPolicies
{
    /// <summary>Login and other credential endpoints: per client IP.</summary>
    public const string Authentication = "authentication";
}

/// <summary>Cookie and header names for dashboard sessions and CSRF protection.</summary>
public static class AuthCookies
{
    /// <summary>HttpOnly session cookie. The __Host- prefix forces Secure, Path=/ and no Domain.</summary>
    public const string Session = "__Host-payflow.session";

    /// <summary>HttpOnly antiforgery cookie token.</summary>
    public const string Antiforgery = "__Host-payflow.af";

    /// <summary>JS-readable request token that Angular echoes back in <see cref="XsrfHeader"/>.</summary>
    public const string XsrfToken = "XSRF-TOKEN";

    public const string XsrfHeader = "X-XSRF-TOKEN";
}

public sealed record MerchantMembershipInfo(Guid MerchantId, string Role);

/// <summary>The signed-in dashboard user for the current request (tenant context).</summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }

    Guid? UserId { get; }

    bool IsPlatformAdmin { get; }

    IReadOnlyList<MerchantMembershipInfo> Memberships { get; }

    /// <summary>True if the user is a member of <paramref name="merchantId"/> with one of <paramref name="roles"/> (any role when empty).</summary>
    bool HasMerchantRole(Guid merchantId, params string[] roles);
}
