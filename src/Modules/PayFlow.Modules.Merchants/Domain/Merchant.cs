using PayFlow.Modules.Merchants.Contracts;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Merchants.Domain;

/// <summary>A currency the merchant may transact in, with its limits (MER-6).</summary>
internal sealed class MerchantCurrency
{
    public Guid MerchantId { get; init; }

    public required string Currency { get; init; }

    /// <summary>Largest single payment accepted.</summary>
    public decimal MaxPaymentAmount { get; set; }

    /// <summary>Largest single refund a MerchantOperator may initiate; null means operators may not refund.</summary>
    public decimal? OperatorRefundLimit { get; set; }
}

/// <summary>
/// Merchant aggregate. Status follows a whitelist (MER-2):
/// PendingActivation → Active | Closed; Active → Suspended | Closed; Suspended → Active | Closed.
/// </summary>
internal sealed class Merchant
{
    private static readonly Dictionary<MerchantStatus, MerchantStatus[]> AllowedTransitions = new()
    {
        [MerchantStatus.PendingActivation] = [MerchantStatus.Active, MerchantStatus.Closed],
        [MerchantStatus.Active] = [MerchantStatus.Suspended, MerchantStatus.Closed],
        [MerchantStatus.Suspended] = [MerchantStatus.Active, MerchantStatus.Closed],
        [MerchantStatus.Closed] = [],
    };

    private readonly List<MerchantCurrency> _currencies = [];

    private Merchant()
    {
    }

    public Guid Id { get; private init; }

    public string LegalName { get; private set; } = string.Empty;

    public string TradingName { get; private set; } = string.Empty;

    public string ContactEmail { get; private set; } = string.Empty;

    public string CountryCode { get; private set; } = string.Empty;

    public MerchantStatus Status { get; private set; }

    public string? StatusReason { get; private set; }

    public Guid FeePlanId { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>PostgreSQL xmin, used for optimistic concurrency.</summary>
    public uint Version { get; private set; }

    public IReadOnlyCollection<MerchantCurrency> Currencies => _currencies;

    public static Merchant Register(Guid id, string legalName, string tradingName, string contactEmail, string countryCode, Guid feePlanId, DateTimeOffset now) => new()
    {
        Id = id,
        LegalName = legalName.Trim(),
        TradingName = tradingName.Trim(),
        ContactEmail = contactEmail.Trim(),
        CountryCode = countryCode.ToUpperInvariant(),
        Status = MerchantStatus.PendingActivation,
        FeePlanId = feePlanId,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public static bool CanTransition(MerchantStatus from, MerchantStatus to) => AllowedTransitions[from].Contains(to);

    public Result ChangeStatus(MerchantStatus target, string? reason, DateTimeOffset now)
    {
        if (!CanTransition(Status, target))
        {
            return Error.Unprocessable("invalid_status_transition", $"A merchant cannot move from {Status} to {target}.");
        }

        if (target is MerchantStatus.Suspended or MerchantStatus.Closed && string.IsNullOrWhiteSpace(reason))
        {
            return Error.Validation("reason_required", $"A reason is required to move a merchant to {target}.");
        }

        Status = target;
        StatusReason = reason?.Trim();
        UpdatedAt = now;
        return Result.Success();
    }

    public Result UpdateProfile(string tradingName, string contactEmail, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
        {
            return ClosedError();
        }

        TradingName = tradingName.Trim();
        ContactEmail = contactEmail.Trim();
        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Enables or updates a currency. Limits must be valid amounts in that currency and allowed by the fee plan.</summary>
    public Result ConfigureCurrency(Currency currency, decimal maxPaymentAmount, decimal? operatorRefundLimit, FeePlan feePlan, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
        {
            return ClosedError();
        }

        var terms = feePlan.TermsFor(currency.Code);
        if (terms is null)
        {
            return Error.Unprocessable("currency_not_in_fee_plan", $"Fee plan '{feePlan.Name}' v{feePlan.Version} has no terms for {currency.Code}.");
        }

        if (!Money.HasValidPrecision(maxPaymentAmount, currency) ||
            (operatorRefundLimit is { } limit && !Money.HasValidPrecision(limit, currency)))
        {
            return Error.Validation("amount_precision_exceeded", $"{currency.Code} amounts allow at most {currency.MinorUnits} decimal places.");
        }

        if (maxPaymentAmount < terms.MinPaymentAmount)
        {
            return Error.Validation("max_below_minimum", $"Maximum payment must be at least the fee plan minimum of {terms.MinPaymentAmount} {currency.Code}.");
        }

        if (operatorRefundLimit is < 0)
        {
            return Error.Validation("invalid_refund_limit", "Operator refund limit cannot be negative.");
        }

        var existing = _currencies.SingleOrDefault(c => c.Currency == currency.Code);
        if (existing is null)
        {
            _currencies.Add(new MerchantCurrency
            {
                MerchantId = Id,
                Currency = currency.Code,
                MaxPaymentAmount = maxPaymentAmount,
                OperatorRefundLimit = operatorRefundLimit,
            });
        }
        else
        {
            existing.MaxPaymentAmount = maxPaymentAmount;
            existing.OperatorRefundLimit = operatorRefundLimit;
        }

        UpdatedAt = now;
        return Result.Success();
    }

    /// <summary>Moves the merchant to another fee plan. Every enabled currency must be covered by it.</summary>
    public Result ChangeFeePlan(FeePlan feePlan, DateTimeOffset now)
    {
        if (Status == MerchantStatus.Closed)
        {
            return ClosedError();
        }

        var uncovered = _currencies.Where(c => feePlan.TermsFor(c.Currency) is null).Select(c => c.Currency).ToList();
        if (uncovered.Count > 0)
        {
            return Error.Unprocessable("currency_not_in_fee_plan", $"Fee plan does not cover: {string.Join(", ", uncovered)}.");
        }

        FeePlanId = feePlan.Id;
        UpdatedAt = now;
        return Result.Success();
    }

    private static Error ClosedError() => Error.Unprocessable("merchant_closed", "Closed merchants cannot be changed.");
}
