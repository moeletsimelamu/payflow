using System.Globalization;
using PayFlow.Modules.Merchants.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Merchants.Application;

internal static class Mapping
{
    public static string Format(decimal amount, string currency) => Money.Of(amount, currency).ToAmountString();

    public static MerchantResponse ToResponse(this Merchant m) => new(
        PublicId.Create(IdPrefixes.Merchant, m.Id),
        m.LegalName,
        m.TradingName,
        m.ContactEmail,
        m.CountryCode,
        m.Status,
        m.StatusReason,
        PublicId.Create(IdPrefixes.FeePlan, m.FeePlanId),
        m.Currencies.OrderBy(c => c.Currency).Select(ToResponse).ToList(),
        m.CreatedAt,
        m.UpdatedAt);

    public static MerchantCurrencyResponse ToResponse(this MerchantCurrency c) => new(
        c.Currency,
        Format(c.MaxPaymentAmount, c.Currency),
        c.OperatorRefundLimit is { } limit ? Format(limit, c.Currency) : null);

    public static FeePlanResponse ToResponse(this FeePlan p) => new(
        PublicId.Create(IdPrefixes.FeePlan, p.Id),
        p.Name,
        p.Version,
        p.PercentageRate.ToString("0.######", CultureInfo.InvariantCulture),
        p.Terms.OrderBy(t => t.Currency)
            .Select(t => new FeePlanTermsResponse(t.Currency, Format(t.FixedFee, t.Currency), Format(t.MinPaymentAmount, t.Currency)))
            .ToList(),
        p.CreatedAt);

    public static ApiKeyResponse ToResponse(this ApiKey k, DateTimeOffset now) => new(
        PublicId.Create(IdPrefixes.ApiKey, k.Id),
        k.Name,
        k.Prefix,
        k.StateAt(now).ToString(),
        k.CreatedAt,
        k.CreatedBy,
        k.LastUsedAt,
        k.ExpiresAt,
        k.RevokedAt);

    /// <summary>Parses a decimal-string amount for <paramref name="currencyCode"/>; null input stays null.</summary>
    public static Result<decimal?> ParseAmount(string? value, string currencyCode, string field)
    {
        if (value is null)
        {
            return Result<decimal?>.Success(null);
        }

        if (!Currency.TryFromCode(currencyCode, out var currency))
        {
            return UnsupportedCurrency(currencyCode);
        }

        return Money.TryParse(value, currency, out var money)
            ? Result<decimal?>.Success(money.Amount)
            : Error.Validation("invalid_amount", $"{field} must be a decimal string with at most {currency.MinorUnits} decimal places for {currency.Code}.");
    }

    public static Error UnsupportedCurrency(string code) => Error.Validation("unsupported_currency", $"Currency '{code}' is not supported.");

    public static Error MerchantNotFound() => Error.NotFound("merchant_not_found", "Merchant not found.");
}
