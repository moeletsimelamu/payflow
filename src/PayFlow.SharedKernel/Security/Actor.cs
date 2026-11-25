using System.Net;

namespace PayFlow.SharedKernel.Security;

public enum ActorType
{
    Anonymous,
    User,
    ApiKey,
    System,
}

/// <summary>Who performed an action. Recorded on every audit event and status-history row.</summary>
public sealed record Actor(ActorType Type, string? Id, string Display)
{
    public static Actor Anonymous { get; } = new(ActorType.Anonymous, null, "anonymous");

    public static Actor System(string component) => new(ActorType.System, component, $"system:{component}");
}

/// <summary>Resolves the actor and request metadata for the current unit of work.</summary>
public interface IActorContext
{
    Actor Actor { get; }

    IPAddress? IpAddress { get; }

    string? UserAgent { get; }
}

/// <summary>Claim types shared by every authentication scheme in PayFlow.</summary>
public static class PayFlowClaimTypes
{
    /// <summary>Value of <see cref="ActorType"/> for the principal.</summary>
    public const string ActorType = "pf:actor_type";

    /// <summary>Public id of the principal (for example <c>usr_...</c>).</summary>
    public const string PublicId = "pf:public_id";

    /// <summary>One claim per merchant membership, formatted <c>{merchantGuid}:{role}</c>.</summary>
    public const string Membership = "pf:membership";

    public const string DisplayName = "pf:display_name";

    /// <summary>Present ("true") when the user has an authenticator enrolled.</summary>
    public const string MfaEnabled = "pf:mfa_enabled";

    /// <summary>Merchant a machine principal (API key) acts for.</summary>
    public const string MerchantId = "pf:merchant_id";
}
