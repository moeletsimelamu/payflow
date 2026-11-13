using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Webhooks.Domain;

/// <summary>A merchant's HTTPS receiver (WH-1). Never deleted: disable it instead, so delivery history stays intact.</summary>
internal sealed class WebhookEndpoint
{
    private WebhookEndpoint()
    {
    }

    public Guid Id { get; private init; }

    public Guid MerchantId { get; private init; }

    public string Url { get; private set; } = string.Empty;

    public string? Description { get; private set; }

    public string[] EventTypes { get; private set; } = [];

    public bool IsEnabled { get; private set; }

    /// <summary>Signing secret, encrypted with the master key (it must be readable to sign).</summary>
    public string SecretCiphertext { get; private set; } = string.Empty;

    /// <summary>Last 4 characters, for display.</summary>
    public string SecretHint { get; private set; } = string.Empty;

    public string? PreviousSecretCiphertext { get; private set; }

    public DateTimeOffset? PreviousSecretExpiresAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public uint Version { get; private set; }

    public static WebhookEndpoint Create(Guid id, Guid merchantId, string url, string? description, IEnumerable<string> eventTypes, string secretCiphertext, string secretHint, DateTimeOffset now) => new()
    {
        Id = id,
        MerchantId = merchantId,
        Url = url,
        Description = description,
        EventTypes = Normalise(eventTypes),
        IsEnabled = true,
        SecretCiphertext = secretCiphertext,
        SecretHint = secretHint,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public bool Subscribes(string eventType) => EventTypes.Contains(WebhookEventTypes.All) || EventTypes.Contains(eventType);

    public void Update(string? url, string? description, IEnumerable<string>? eventTypes, bool? enabled, DateTimeOffset now)
    {
        Url = url ?? Url;
        Description = description ?? Description;
        EventTypes = eventTypes is null ? EventTypes : Normalise(eventTypes);
        IsEnabled = enabled ?? IsEnabled;
        UpdatedAt = now;
    }

    /// <summary>The old secret keeps signing (alongside the new one) until <paramref name="overlap"/> has passed.</summary>
    public void RollSecret(string newCiphertext, string newHint, TimeSpan overlap, DateTimeOffset now)
    {
        PreviousSecretCiphertext = SecretCiphertext;
        PreviousSecretExpiresAt = now + overlap;
        SecretCiphertext = newCiphertext;
        SecretHint = newHint;
        UpdatedAt = now;
    }

    /// <summary>Current secret first, then the previous one while it is still in its overlap window.</summary>
    public IEnumerable<string> SigningSecretCiphertexts(DateTimeOffset now)
    {
        yield return SecretCiphertext;
        if (PreviousSecretCiphertext is not null && PreviousSecretExpiresAt > now)
        {
            yield return PreviousSecretCiphertext;
        }
    }

    private static string[] Normalise(IEnumerable<string> eventTypes) => eventTypes.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
}

/// <summary>An immutable event (WH-3). <see cref="Payload"/> is the exact body sent to every endpoint, on every attempt.</summary>
internal sealed class WebhookEvent
{
    public Guid Id { get; init; }

    public Guid MerchantId { get; init; }

    public required string Type { get; init; }

    public required string ApiVersion { get; init; }

    public required string Payload { get; init; }

    public DateTimeOffset OccurredAt { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The outbox message this event came from; unique, so one domain change makes one event. Null for pings.</summary>
    public Guid? SourceMessageId { get; init; }
}

public enum DeliveryStatus
{
    Pending,
    Retrying,
    Succeeded,
    Exhausted,
    Cancelled,
}

/// <summary>One event to one endpoint, and its retry state (WH-4).</summary>
internal sealed class WebhookDelivery
{
    private WebhookDelivery()
    {
    }

    public Guid Id { get; private init; }

    public Guid EventId { get; private init; }

    public Guid EndpointId { get; private init; }

    public Guid MerchantId { get; private init; }

    public DeliveryStatus Status { get; private set; }

    public int AttemptCount { get; private set; }

    /// <summary>Attempts made before the latest manual redelivery; the retry budget counts from here.</summary>
    public int AttemptBudgetStart { get; private set; }

    public DateTimeOffset NextAttemptAt { get; private set; }

    public DateTimeOffset? LastAttemptAt { get; private set; }

    public DateTimeOffset? SucceededAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public uint Version { get; private set; }

    public static WebhookDelivery Schedule(Guid id, Guid eventId, Guid endpointId, Guid merchantId, DateTimeOffset now) => new()
    {
        Id = id,
        EventId = eventId,
        EndpointId = endpointId,
        MerchantId = merchantId,
        Status = DeliveryStatus.Pending,
        NextAttemptAt = now,
        CreatedAt = now,
    };

    public int NextAttemptNumber => AttemptCount + 1;

    public void RecordSuccess(DateTimeOffset now)
    {
        AttemptCount++;
        LastAttemptAt = now;
        SucceededAt = now;
        Status = DeliveryStatus.Succeeded;
    }

    public void RecordFailure(DateTimeOffset now, int jitterPercent)
    {
        AttemptCount++;
        LastAttemptAt = now;
        var delay = RetrySchedule.DelayAfter(AttemptCount - AttemptBudgetStart, jitterPercent);
        if (delay is null)
        {
            Status = DeliveryStatus.Exhausted;
            return;
        }

        Status = DeliveryStatus.Retrying;
        NextAttemptAt = now + delay.Value;
    }

    public void Cancel(DateTimeOffset now)
    {
        Status = DeliveryStatus.Cancelled;
        LastAttemptAt ??= now;
    }

    /// <summary>Manual redelivery (WH-4): sends again now, with a fresh retry budget.</summary>
    public Result Redeliver(DateTimeOffset now)
    {
        if (Status is DeliveryStatus.Pending or DeliveryStatus.Retrying)
        {
            return Error.Conflict("delivery_in_progress", "This delivery is already scheduled.");
        }

        Status = DeliveryStatus.Pending;
        AttemptBudgetStart = AttemptCount;
        NextAttemptAt = now;
        return Result.Success();
    }
}

public enum DeliveryErrorKind
{
    Non2xx,
    Timeout,
    ConnectionFailed,
    SsrfBlocked,
    EndpointDisabled,
}

/// <summary>An immutable record of one HTTP attempt (WH-5).</summary>
internal sealed class DeliveryAttempt
{
    public long Id { get; init; }

    public Guid DeliveryId { get; init; }

    public int AttemptNumber { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public long DurationMs { get; init; }

    /// <summary>Request headers as sent, with the signature value masked (jsonb).</summary>
    public required string RequestHeaders { get; init; }

    public int? ResponseStatus { get; init; }

    public string? ResponseBodyExcerpt { get; init; }

    public DeliveryErrorKind? ErrorKind { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Delay after each failed attempt (docs/PAYMENT_LIFECYCLE.md §9.3): 1 min, 5 min, 30 min, 2 h,
/// 5 h, 10 h, 10 h, so 8 attempts over about 28 h, each delay jittered by ±20 % to spread retry storms.
/// </summary>
internal static class RetrySchedule
{
    public const int MaxAttempts = 8;

    public static IReadOnlyList<TimeSpan> Delays { get; } =
    [
        TimeSpan.FromMinutes(1),
        TimeSpan.FromMinutes(5),
        TimeSpan.FromMinutes(30),
        TimeSpan.FromHours(2),
        TimeSpan.FromHours(5),
        TimeSpan.FromHours(10),
        TimeSpan.FromHours(10),
    ];

    /// <summary>Delay before the next attempt after <paramref name="failedAttempts"/> failures, or null when the budget is spent.</summary>
    public static TimeSpan? DelayAfter(int failedAttempts, int jitterPercent)
    {
        if (jitterPercent is < -20 or > 20)
        {
            throw new ArgumentOutOfRangeException(nameof(jitterPercent), "Jitter must be within ±20 %.");
        }

        if (failedAttempts < 1 || failedAttempts >= MaxAttempts)
        {
            return null;
        }

        var baseDelay = Delays[failedAttempts - 1];
        return TimeSpan.FromTicks(baseDelay.Ticks + (baseDelay.Ticks * jitterPercent / 100));
    }
}
