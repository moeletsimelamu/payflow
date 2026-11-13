using System.Text.RegularExpressions;

namespace PayFlow.Modules.Payments.Domain;

/// <summary>
/// Detects anything that looks like a real card number (13–19 digits, optionally separated by
/// spaces or dashes, passing the Luhn check). PayFlow is a simulation and refuses such data in every
/// field, so it can never be stored or logged.
/// </summary>
internal static partial class CardDataDetector
{
    public static bool ContainsCardNumber(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        foreach (Match candidate in DigitRun().Matches(text))
        {
            var digits = new string(candidate.Value.Where(char.IsAsciiDigit).ToArray());
            if (digits.Length is >= 13 and <= 19 && PassesLuhn(digits))
            {
                return true;
            }
        }

        return false;
    }

    public static bool PassesLuhn(string digits)
    {
        var sum = 0;
        var doubleIt = false;
        for (var i = digits.Length - 1; i >= 0; i--)
        {
            var d = digits[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    // Runs of 13–19 digits where single spaces or dashes may separate digits.
    [GeneratedRegex(@"(?<!\d)\d(?:[ -]?\d){12,18}(?!\d)", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex DigitRun();
}
