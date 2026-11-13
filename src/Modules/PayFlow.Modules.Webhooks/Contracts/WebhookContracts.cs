namespace PayFlow.Modules.Webhooks.Contracts;

/// <summary>Event types merchants can subscribe to (WH-2). <c>*</c> subscribes to all.</summary>
public static class WebhookEventTypes
{
    public const string All = "*";
    public const string Ping = "webhook.ping";

    /// <summary>Outbox message types that become merchant webhook events.</summary>
    public static IReadOnlySet<string> Deliverable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        "payment.pending",
        "payment.succeeded",
        "payment.declined",
        "payment.failed",
        "payment.expired",
        "payment.reversed",
        "refund.succeeded",
        "refund.failed",
    };

    public static IReadOnlySet<string> Subscribable { get; } = new HashSet<string>(Deliverable.Append(Ping).Append(All), StringComparer.Ordinal);
}

/// <summary>Headers PayFlow sends with every delivery.</summary>
public static class WebhookHeaders
{
    public const string EventId = "PayFlow-Event-Id";
    public const string EventType = "PayFlow-Event-Type";
    public const string DeliveryId = "PayFlow-Delivery-Id";
    public const string Attempt = "PayFlow-Delivery-Attempt";
}

public sealed class WebhookOptions
{
    public const string Section = "Webhooks";

    /// <summary>Allow plain-HTTP endpoint URLs. Development only (the Compose demo sink).</summary>
    public bool AllowHttp { get; set; }

    /// <summary>Allow private, loopback and link-local destinations. Development only. Disables the SSRF guard.</summary>
    public bool AllowPrivateNetworks { get; set; }

    public int MaxEndpointsPerMerchant { get; set; } = 5;

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>A claimed delivery is invisible to other senders for this long (covers the HTTP call).</summary>
    public TimeSpan ClaimLease { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>After a secret roll, deliveries are signed with both secrets for this long.</summary>
    public TimeSpan SecretRotationOverlap { get; set; } = TimeSpan.FromHours(24);

    public string ApiVersion { get; set; } = "2026-11-01";
}
