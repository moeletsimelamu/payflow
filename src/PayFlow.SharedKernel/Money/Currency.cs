using System.Diagnostics.CodeAnalysis;

namespace PayFlow.SharedKernel.Money;

/// <summary>
/// An ISO 4217 currency supported by PayFlow, with its number of minor units (decimal places).
/// Instances are only obtainable from the static members, so an unknown currency can never enter
/// the domain.
/// </summary>
public sealed class Currency : IEquatable<Currency>
{
    public static readonly Currency ZAR = new("ZAR", 2, "South African Rand");
    public static readonly Currency USD = new("USD", 2, "US Dollar");
    public static readonly Currency EUR = new("EUR", 2, "Euro");
    public static readonly Currency GBP = new("GBP", 2, "Pound Sterling");
    public static readonly Currency JPY = new("JPY", 0, "Japanese Yen");
    public static readonly Currency KWD = new("KWD", 3, "Kuwaiti Dinar");

    private static readonly Dictionary<string, Currency> ByCode =
        new[] { ZAR, USD, EUR, GBP, JPY, KWD }.ToDictionary(c => c.Code, StringComparer.Ordinal);

    private Currency(string code, int minorUnits, string displayName)
    {
        Code = code;
        MinorUnits = minorUnits;
        DisplayName = displayName;
    }

    /// <summary>Upper-case three-letter ISO 4217 code.</summary>
    public string Code { get; }

    /// <summary>Digits after the decimal separator (0 for JPY, 2 for ZAR, 3 for KWD).</summary>
    public int MinorUnits { get; }

    public string DisplayName { get; }

    public static IReadOnlyCollection<Currency> All => ByCode.Values;

    /// <summary>Resolves a currency code. Codes are case-sensitive: only upper-case ISO codes are accepted.</summary>
    public static bool TryFromCode(string? code, [NotNullWhen(true)] out Currency? currency)
    {
        currency = null;
        return code is not null && ByCode.TryGetValue(code, out currency);
    }

    public static Currency FromCode(string code) =>
        TryFromCode(code, out var currency)
            ? currency
            : throw new ArgumentException($"Unsupported currency '{code}'.", nameof(code));

    public bool Equals(Currency? other) => other is not null && string.Equals(Code, other.Code, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is Currency other && Equals(other);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Code);

    public override string ToString() => Code;

    public static bool operator ==(Currency? left, Currency? right) => Equals(left, right);

    public static bool operator !=(Currency? left, Currency? right) => !Equals(left, right);
}
