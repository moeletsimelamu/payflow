using PayFlow.Modules.Merchants.Contracts;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Merchants.Application;

// Requests. Monetary values are decimal strings ("100.00") per rule M6.
public sealed record MerchantCurrencyRequest(string Currency, string MaxPaymentAmount, string? OperatorRefundLimit);

public sealed record RegisterMerchantRequest(
    string LegalName,
    string TradingName,
    string ContactEmail,
    string CountryCode,
    IReadOnlyList<MerchantCurrencyRequest> Currencies,
    string? FeePlanId,
    string? AdminEmail,
    string? AdminDisplayName);

public sealed record UpdateMerchantProfileRequest(string TradingName, string ContactEmail);

public sealed record ChangeMerchantStatusRequest(MerchantStatus Status, string? Reason);

public sealed record CurrencyLimitsRequest(string MaxPaymentAmount, string? OperatorRefundLimit);

public sealed record ChangeFeePlanRequest(string FeePlanId);

public sealed record FeePlanTermsRequest(string Currency, string FixedFee, string MinPaymentAmount);

public sealed record CreateFeePlanRequest(string Name, string PercentageRate, IReadOnlyList<FeePlanTermsRequest> Terms);

public sealed record CreateApiKeyRequest(string Name);

public sealed record RotateApiKeyRequest(int? GracePeriodHours);

public sealed record InviteMemberRequest(string Email, string DisplayName, string Role);

public sealed record ChangeMemberRoleRequest(string Role);

// Responses.
public sealed record MerchantCurrencyResponse(string Currency, string MaxPaymentAmount, string? OperatorRefundLimit);

public sealed record MerchantResponse(
    string Id,
    string LegalName,
    string TradingName,
    string ContactEmail,
    string CountryCode,
    MerchantStatus Status,
    string? StatusReason,
    string FeePlanId,
    IReadOnlyList<MerchantCurrencyResponse> Currencies,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record MemberInvitationResponse(string UserId, string Email, string Role, [property: Sensitive] string? InvitationToken);

public sealed record RegisterMerchantResponse(MerchantResponse Merchant, MemberInvitationResponse? AdminInvitation);

public sealed record FeePlanTermsResponse(string Currency, string FixedFee, string MinPaymentAmount);

public sealed record FeePlanResponse(string Id, string Name, int Version, string PercentageRate, IReadOnlyList<FeePlanTermsResponse> Terms, DateTimeOffset CreatedAt);

public sealed record ApiKeyResponse(
    string Id,
    string Name,
    string Prefix,
    string State,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? LastUsedAt,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt);

/// <summary>Returned only when a key is created or rotated. <see cref="Secret"/> is never shown again.</summary>
public sealed record CreatedApiKeyResponse(ApiKeyResponse Key, [property: Sensitive] string Secret, string Notice);

public sealed record MemberResponse(string UserId, string Email, string DisplayName, string Role, DateTimeOffset AddedAt, bool HasAcceptedInvitation);

public sealed record ActivityItemResponse(
    long Id,
    DateTimeOffset OccurredAt,
    string ActorType,
    string? ActorId,
    string ActorDisplay,
    string Action,
    string TargetType,
    string? TargetId,
    string? Changes,
    string CorrelationId);

public sealed record ActivityResponse(IReadOnlyList<ActivityItemResponse> Items, long? NextBefore);

public sealed record PagedResponse<T>(IReadOnlyList<T> Items, int Page, int PageSize, int Total);

/// <summary>What a merchant's own API key sees on <c>GET /v1/merchant</c>.</summary>
public sealed record MerchantApiProfileResponse(
    string Id,
    string TradingName,
    MerchantStatus Status,
    IReadOnlyList<MerchantCurrencyResponse> Currencies,
    bool Simulated);
