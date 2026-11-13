using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Merchants.Domain;

/// <summary>Per-currency fee terms of a fee plan.</summary>
internal sealed class FeePlanCurrencyTerms
{
    public Guid FeePlanId { get; init; }

    public required string Currency { get; init; }

    public decimal FixedFee { get; init; }

    public decimal MinPaymentAmount { get; init; }
}

/// <summary>
/// An immutable, versioned fee plan (docs/PAYMENT_LIFECYCLE.md §2). Changing pricing creates a new
/// version; payments snapshot the terms, so history never changes.
/// </summary>
internal sealed class FeePlan
{
    public static readonly Guid StandardV1Id = new("0192f000-0000-7000-8000-000000000001");

    private readonly List<FeePlanCurrencyTerms> _terms = [];

    private FeePlan()
    {
    }

    public Guid Id { get; private init; }

    public string Name { get; private init; } = string.Empty;

    public int Version { get; private init; }

    /// <summary>Percentage as a fraction: 0.029 means 2.9 %.</summary>
    public decimal PercentageRate { get; private init; }

    public DateTimeOffset CreatedAt { get; private init; }

    public IReadOnlyCollection<FeePlanCurrencyTerms> Terms => _terms;

    public FeePlanCurrencyTerms? TermsFor(string currency) => _terms.SingleOrDefault(t => t.Currency == currency);

    public static Result<FeePlan> Create(
        Guid id,
        string name,
        int version,
        decimal percentageRate,
        IEnumerable<(Currency Currency, decimal FixedFee, decimal MinPaymentAmount)> terms,
        DateTimeOffset now)
    {
        if (percentageRate is < 0m or >= 1m)
        {
            return Error.Validation("invalid_rate", "Percentage rate must be at least 0 and below 1 (0.029 = 2.9 %).");
        }

        if (decimal.Round(percentageRate, 6) != percentageRate)
        {
            return Error.Validation("invalid_rate", "Percentage rate allows at most 6 decimal places.");
        }

        var plan = new FeePlan { Id = id, Name = name.Trim(), Version = version, PercentageRate = percentageRate, CreatedAt = now };
        foreach (var (currency, fixedFee, minimum) in terms)
        {
            if (plan._terms.Any(t => t.Currency == currency.Code))
            {
                return Error.Validation("duplicate_currency", $"{currency.Code} appears more than once.");
            }

            if (!Money.HasValidPrecision(fixedFee, currency) || !Money.HasValidPrecision(minimum, currency))
            {
                return Error.Validation("amount_precision_exceeded", $"{currency.Code} amounts allow at most {currency.MinorUnits} decimal places.");
            }

            // Guarantees fee < amount for every accepted payment: fee <= amount × rate + fixed < amount
            // whenever amount >= minimum > fixed / (1 − rate).
            if (fixedFee < 0m || minimum <= 0m || minimum * (1m - percentageRate) <= fixedFee)
            {
                return Error.Validation("fee_exceeds_minimum", $"{currency.Code}: the minimum payment must exceed the fee charged on it.");
            }

            plan._terms.Add(new FeePlanCurrencyTerms { FeePlanId = id, Currency = currency.Code, FixedFee = fixedFee, MinPaymentAmount = minimum });
        }

        return plan._terms.Count == 0
            ? Error.Validation("terms_required", "A fee plan needs terms for at least one currency.")
            : plan;
    }

    /// <summary>The seeded default plan: 2.9 % plus a fixed fee per currency.</summary>
    public static FeePlan StandardV1(DateTimeOffset createdAt) => Create(
        StandardV1Id,
        "Standard",
        1,
        0.029m,
        [
            (Currency.ZAR, 2.00m, 5.00m),
            (Currency.USD, 0.30m, 1.00m),
            (Currency.EUR, 0.25m, 1.00m),
            (Currency.GBP, 0.20m, 1.00m),
            (Currency.JPY, 30m, 100m),
            (Currency.KWD, 0.100m, 1.000m),
        ],
        createdAt).Value;
}
