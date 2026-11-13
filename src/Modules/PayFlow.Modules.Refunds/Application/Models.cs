using System.Text.Json.Serialization;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;

namespace PayFlow.Modules.Refunds.Application;

/// <summary><c>amount</c> omitted means "refund everything still refundable".</summary>
public sealed record CreateRefundRequest(string? Amount, string? Reason);

public sealed record CreateReversalRequest(string? Reason);

public sealed record RefundResponse(
    string Id,
    [property: JsonPropertyName("object")] string ObjectType,
    string PaymentId,
    string Amount,
    string Currency,
    RefundStatus Status,
    string? Reason,
    string? FailureReason,
    string RequestedByType,
    string? RequestedById,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt,
    bool Simulated);

public sealed record ReversalResponse(
    string Id,
    [property: JsonPropertyName("object")] string ObjectType,
    string PaymentId,
    string Amount,
    string FeeReturned,
    string Currency,
    string Reason,
    string RequestedByType,
    string? RequestedById,
    DateTimeOffset CreatedAt,
    bool Simulated);

public sealed record PaymentRefundsResponse(string PaymentId, IReadOnlyList<RefundResponse> Refunds, ReversalResponse? Reversal);

/// <summary>Stored or fresh response of an idempotent refund/reversal request.</summary>
public sealed record IdempotentResponse(int StatusCode, string Body, bool Replayed, string? Location);

/// <summary>Who is asking, which decides the refund limits (rule R6).</summary>
internal enum RefundChannel
{
    /// <summary>The merchant's own server, authenticated by API key. No per-refund limit.</summary>
    MerchantApi,

    /// <summary>A MerchantAdmin on the dashboard. No per-refund limit.</summary>
    MerchantAdmin,

    /// <summary>A MerchantOperator on the dashboard: limited by the merchant's operator refund limit.</summary>
    MerchantOperator,
}

internal static class RefundMapping
{
    public static RefundResponse ToResponse(this Refund r) => new(
        PublicId.Create(IdPrefixes.Refund, r.Id),
        "refund",
        PublicId.Create(IdPrefixes.Payment, r.PaymentId),
        Money.Of(r.Amount, r.Currency).ToAmountString(),
        r.Currency,
        r.Status,
        r.Reason,
        r.FailureReason,
        r.RequestedByType,
        r.RequestedById,
        r.CreatedAt,
        r.CompletedAt,
        Simulated: true);

    public static ReversalResponse ToResponse(this Reversal r) => new(
        PublicId.Create(IdPrefixes.Reversal, r.Id),
        "reversal",
        PublicId.Create(IdPrefixes.Payment, r.PaymentId),
        Money.Of(r.Amount, r.Currency).ToAmountString(),
        Money.Of(r.FeeReturned, r.Currency).ToAmountString(),
        r.Currency,
        r.Reason,
        r.RequestedByType,
        r.RequestedById,
        r.CreatedAt,
        Simulated: true);

    public static RefundEvent ToEvent(this Refund r, DateTimeOffset now) => new(
        PublicId.Create(IdPrefixes.Refund, r.Id),
        PublicId.Create(IdPrefixes.Payment, r.PaymentId),
        PublicId.Create(IdPrefixes.Merchant, r.MerchantId),
        r.Status,
        Money.Of(r.Amount, r.Currency).ToAmountString(),
        r.Currency,
        r.FailureReason,
        now);

    public static ReversalEvent ToEvent(this Reversal r) => new(
        PublicId.Create(IdPrefixes.Reversal, r.Id),
        PublicId.Create(IdPrefixes.Payment, r.PaymentId),
        PublicId.Create(IdPrefixes.Merchant, r.MerchantId),
        Money.Of(r.Amount, r.Currency).ToAmountString(),
        Money.Of(r.FeeReturned, r.Currency).ToAmountString(),
        r.Currency,
        r.CreatedAt);
}
