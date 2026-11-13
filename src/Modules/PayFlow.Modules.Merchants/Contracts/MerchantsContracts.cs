namespace PayFlow.Modules.Merchants.Contracts;

public enum MerchantStatus
{
    PendingActivation,
    Active,
    Suspended,
    Closed,
}

/// <summary>Authentication scheme and policy for the merchant API (<c>/v1</c>).</summary>
public static class MerchantApiAuth
{
    public const string Scheme = "ApiKey";
    public const string Policy = "MerchantApi";

    /// <summary>Every merchant API key starts with this. "test" because PayFlow only simulates payments.</summary>
    public const string KeyPrefix = "pf_test_sk_";
}

/// <summary>Fee and limit terms for one currency, as needed to accept a payment.</summary>
public sealed record MerchantCurrencyTerms(
    string Currency,
    decimal MaxPaymentAmount,
    decimal? OperatorRefundLimit,
    Guid FeePlanId,
    decimal PercentageRate,
    decimal FixedFee,
    decimal MinPaymentAmount);

/// <summary><see cref="Terms"/> is null when the currency is not enabled for the merchant.</summary>
public sealed record MerchantPaymentProfile(Guid MerchantId, MerchantStatus Status, MerchantCurrencyTerms? Terms);

/// <summary>Read access to merchants for other modules.</summary>
public interface IMerchantDirectory
{
    Task<MerchantPaymentProfile?> GetPaymentProfileAsync(Guid merchantId, string currency, CancellationToken cancellationToken);
}

/// <summary>The merchant an API-key request acts for (tenant context on <c>/v1</c>).</summary>
public interface ICurrentMerchant
{
    Guid? MerchantId { get; }

    Guid? ApiKeyId { get; }
}
