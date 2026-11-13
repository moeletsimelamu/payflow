using System.Globalization;

namespace PayFlow.SharedKernel.Money;

/// <summary>
/// An immutable monetary amount in a specific currency.
/// <para>
/// Financial rules (docs/PAYMENT_LIFECYCLE.md §1):
/// <list type="bullet">
/// <item>M1: backed by <see cref="decimal"/>, never floating point.</item>
/// <item>M2: arithmetic across currencies throws <see cref="CurrencyMismatchException"/>.</item>
/// <item>M4: an amount with more precision than the currency allows is rejected, never silently rounded.</item>
/// <item>M5: rounding happens only through <see cref="MultiplyAndRound"/>, half away from zero.</item>
/// </list>
/// </para>
/// </summary>
public readonly struct Money : IEquatable<Money>, IComparable<Money>
{
    /// <summary>Rounding mode for every calculated amount (commercial "half-up").</summary>
    public const MidpointRounding RoundingMode = MidpointRounding.AwayFromZero;

    private Money(decimal amount, Currency currency)
    {
        // Normalise the scale (10.5 becomes 10.50 for ZAR) so formatting and equality are stable.
        Amount = decimal.Round(amount, currency.MinorUnits, RoundingMode);
        Currency = currency;
    }

    public decimal Amount { get; }

    public Currency Currency { get; }

    public bool IsZero => Amount == 0m;

    public bool IsPositive => Amount > 0m;

    public bool IsNegative => Amount < 0m;

    /// <summary>Creates money from an exact amount. Fails if the amount has more decimals than the currency allows.</summary>
    public static Money Of(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        if (!HasValidPrecision(amount, currency))
        {
            throw new ArgumentException(
                $"Amount {amount.ToString(CultureInfo.InvariantCulture)} has more than {currency.MinorUnits} decimal places allowed for {currency.Code}.",
                nameof(amount));
        }

        return new Money(amount, currency);
    }

    public static Money Of(decimal amount, string currencyCode) => Of(amount, Currency.FromCode(currencyCode));

    public static Money Zero(Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return new Money(0m, currency);
    }

    /// <summary>True when <paramref name="amount"/> is representable in <paramref name="currency"/> without rounding.</summary>
    public static bool HasValidPrecision(decimal amount, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(currency);
        return decimal.Round(amount, currency.MinorUnits, RoundingMode) == amount;
    }

    /// <summary>
    /// Parses an API amount string such as "100.00". Only plain invariant decimal notation is accepted:
    /// no thousands separators, exponents, currency symbols or surrounding whitespace.
    /// </summary>
    public static bool TryParse(string? amount, Currency currency, out Money money)
    {
        ArgumentNullException.ThrowIfNull(currency);
        money = default;
        if (string.IsNullOrEmpty(amount) || amount.Length > 32)
        {
            return false;
        }

        foreach (var c in amount)
        {
            if (!char.IsAsciiDigit(c) && c is not '.' and not '-')
            {
                return false;
            }
        }

        const NumberStyles styles = NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (!decimal.TryParse(amount, styles, CultureInfo.InvariantCulture, out var value) ||
            !HasValidPrecision(value, currency))
        {
            return false;
        }

        money = new Money(value, currency);
        return true;
    }

    /// <summary>
    /// Multiplies by a rate (for example a fee percentage) and rounds once to the currency's minor
    /// units, half away from zero. This is the only sanctioned rounding point in the system.
    /// </summary>
    public Money MultiplyAndRound(decimal rate)
    {
        EnsureInitialised();
        return new Money(Amount * rate, Currency);
    }

    public Money Add(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Negate()
    {
        EnsureInitialised();
        return new Money(-Amount, Currency);
    }

    public static Money Max(Money left, Money right) => left.CompareTo(right) >= 0 ? left : right;

    public static Money Min(Money left, Money right) => left.CompareTo(right) <= 0 ? left : right;

    /// <summary>Sums amounts that must all be in <paramref name="currency"/>.</summary>
    public static Money Sum(IEnumerable<Money> amounts, Currency currency)
    {
        ArgumentNullException.ThrowIfNull(amounts);
        return amounts.Aggregate(Zero(currency), (total, next) => total.Add(next));
    }

    /// <summary>Invariant amount string with exactly the currency's minor units: "100.00", "1000", "12.345".</summary>
    public string ToAmountString()
    {
        EnsureInitialised();
        return Amount.ToString("F" + Currency.MinorUnits.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
    }

    public override string ToString() => Currency is null ? "(uninitialised money)" : $"{Currency.Code} {ToAmountString()}";

    public int CompareTo(Money other)
    {
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    public bool Equals(Money other) => Currency == other.Currency && Amount == other.Amount;

    public override bool Equals(object? obj) => obj is Money other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Amount, Currency);

    public static bool operator ==(Money left, Money right) => left.Equals(right);

    public static bool operator !=(Money left, Money right) => !left.Equals(right);

    public static bool operator <(Money left, Money right) => left.CompareTo(right) < 0;

    public static bool operator >(Money left, Money right) => left.CompareTo(right) > 0;

    public static bool operator <=(Money left, Money right) => left.CompareTo(right) <= 0;

    public static bool operator >=(Money left, Money right) => left.CompareTo(right) >= 0;

    public static Money operator +(Money left, Money right) => left.Add(right);

    public static Money operator -(Money left, Money right) => left.Subtract(right);

    public static Money operator -(Money value) => value.Negate();

    private void EnsureInitialised()
    {
        if (Currency is null)
        {
            throw new InvalidOperationException("Money value is uninitialised (default struct).");
        }
    }

    private void EnsureSameCurrency(Money other)
    {
        EnsureInitialised();
        other.EnsureInitialised();
        if (Currency != other.Currency)
        {
            throw new CurrencyMismatchException(Currency, other.Currency);
        }
    }
}
