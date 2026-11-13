using System.Text.Json;
using System.Text.Json.Serialization;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;

namespace PayFlow.Modules.Payments.Application;

/// <summary><c>POST /v1/payments</c>. Amount is a decimal string (rule M6).</summary>
public sealed record CreatePaymentRequest(
    string Amount,
    string Currency,
    string PaymentMethod,
    string MerchantReference,
    string? Description,
    IReadOnlyDictionary<string, string>? Metadata);

public sealed record PaymentStatusChangeResponse(PaymentStatus? From, PaymentStatus To, string Reason, string ActorType, string? ActorId, DateTimeOffset OccurredAt);

public sealed record PaymentResponse(
    string Id,
    [property: JsonPropertyName("object")] string ObjectType,
    string MerchantReference,
    string Amount,
    string Currency,
    PaymentStatus Status,
    string PaymentMethod,
    string? Fee,
    string? Net,
    string RefundedAmount,
    string RefundableAmount,
    string? FailureCode,
    string? FailureReason,
    string? Description,
    IReadOnlyDictionary<string, string>? Metadata,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? CompletedAt,
    bool Simulated,
    IReadOnlyList<PaymentStatusChangeResponse>? StatusHistory);

public sealed record PaymentListResponse(IReadOnlyList<PaymentResponse> Data, bool HasMore, string? NextStartingAfter);

public sealed record LedgerPostingResponse(string Account, PostingDirection Direction, string Amount);

public sealed record LedgerEntryResponse(string EntryType, string Description, DateTimeOffset OccurredAt, IReadOnlyList<LedgerPostingResponse> Postings);

/// <summary>Dashboard transaction detail: the payment plus its lifecycle, timings and ledger entries.</summary>
public sealed record PaymentDetailResponse(
    PaymentResponse Payment,
    string MerchantId,
    string IdempotencyKey,
    string FeePlanId,
    string FeeRate,
    DateTimeOffset? ProcessingStartedAt,
    long? ProcessingMilliseconds,
    IReadOnlyList<LedgerEntryResponse> Ledger);

public sealed record DashboardPaymentRow(
    string Id,
    string MerchantId,
    string MerchantReference,
    string Amount,
    string Currency,
    PaymentStatus Status,
    string? Fee,
    string? FailureCode,
    DateTimeOffset CreatedAt);

public sealed record DashboardPaymentPage(IReadOnlyList<DashboardPaymentRow> Items, int Page, int PageSize, int Total);

internal static class PaymentMapping
{
    public static PaymentResponse ToResponse(this Payment p, bool includeHistory)
    {
        var money = p.AmountMoney;
        var fee = p.FeeMoney;
        return new PaymentResponse(
            PublicId.Create(IdPrefixes.Payment, p.Id),
            "payment",
            p.MerchantReference,
            money.ToAmountString(),
            p.Currency,
            p.Status,
            p.PaymentMethod,
            fee?.ToAmountString(),
            fee is { } f ? (money - f).ToAmountString() : null,
            Money.Of(p.RefundedAmount, p.Currency).ToAmountString(),
            Money.Of(PaymentStateMachine.IsCaptured(p.Status) && p.Status != PaymentStatus.Reversed ? p.RefundableAmount : 0m, p.Currency).ToAmountString(),
            p.FailureCode,
            p.FailureReason,
            p.Description,
            p.Metadata is null ? null : JsonSerializer.Deserialize<Dictionary<string, string>>(p.Metadata),
            p.CreatedAt,
            p.UpdatedAt,
            p.CompletedAt,
            Simulated: true,
            includeHistory
                ? p.History.OrderBy(h => h.OccurredAt).ThenBy(h => h.Id)
                    .Select(h => new PaymentStatusChangeResponse(h.FromStatus, h.ToStatus, h.Reason, h.ActorType, h.ActorId, h.OccurredAt))
                    .ToList()
                : null);
    }

    public static PaymentEvent ToEvent(this Payment p, DateTimeOffset occurredAt) => new(
        PublicId.Create(IdPrefixes.Payment, p.Id),
        PublicId.Create(IdPrefixes.Merchant, p.MerchantId),
        p.MerchantReference,
        p.Status,
        p.AmountMoney.ToAmountString(),
        p.Currency,
        p.FeeMoney?.ToAmountString(),
        p.FailureCode,
        occurredAt);

    public static string? EventTypeFor(PaymentStatus status) => status switch
    {
        PaymentStatus.Pending => PaymentEventTypes.Pending,
        PaymentStatus.Succeeded => PaymentEventTypes.Succeeded,
        PaymentStatus.Declined => PaymentEventTypes.Declined,
        PaymentStatus.Failed => PaymentEventTypes.Failed,
        PaymentStatus.Expired => PaymentEventTypes.Expired,
        _ => null,
    };
}
