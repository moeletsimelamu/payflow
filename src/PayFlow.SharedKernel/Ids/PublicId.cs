using System.Diagnostics.CodeAnalysis;

namespace PayFlow.SharedKernel.Ids;

/// <summary>
/// Opaque, prefixed public identifiers such as <c>pay_01jc8x...</c>.
/// <para>
/// The suffix is the 128-bit UUID encoded as 26 lower-case Crockford base32 characters, so a public
/// id maps one-to-one onto the primary key and is never stored separately. UUIDv7 keys keep ids
/// time-ordered without exposing sequence numbers or volumes.
/// </para>
/// </summary>
public static class PublicId
{
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";
    private const int EncodedLength = 26;

    public static string Create(string prefix, Guid id)
    {
        ValidatePrefix(prefix);
        var value = ToUInt128(id);
        Span<char> chars = stackalloc char[EncodedLength];
        for (var i = EncodedLength - 1; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(value & 0x1F)];
            value >>= 5;
        }

        return string.Concat(prefix, "_", new string(chars));
    }

    public static bool TryParse(string? value, string expectedPrefix, [NotNullWhen(true)] out Guid? id)
    {
        id = null;
        ValidatePrefix(expectedPrefix);
        if (value is null || value.Length != expectedPrefix.Length + 1 + EncodedLength ||
            !value.StartsWith(expectedPrefix + "_", StringComparison.Ordinal))
        {
            return false;
        }

        var suffix = value.AsSpan(expectedPrefix.Length + 1);

        // 26 chars x 5 bits = 130 bits, so the first char may only carry the top 3 bits of the UUID.
        if (DecodeChar(suffix[0]) is not (>= 0 and <= 7))
        {
            return false;
        }

        UInt128 result = 0;
        foreach (var c in suffix)
        {
            var digit = DecodeChar(c);
            if (digit < 0)
            {
                return false;
            }

            result = (result << 5) | (uint)digit;
        }

        id = FromUInt128(result);
        return true;
    }

    private static int DecodeChar(char c) => Alphabet.IndexOf(c, StringComparison.Ordinal);

    private static void ValidatePrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix) || prefix.Length > 6 || !prefix.All(char.IsAsciiLetterLower))
        {
            throw new ArgumentException("Prefix must be 1 to 6 lower-case ASCII letters.", nameof(prefix));
        }
    }

    private static UInt128 ToUInt128(Guid id)
    {
        Span<byte> bytes = stackalloc byte[16];
        id.TryWriteBytes(bytes, bigEndian: true, out _);
        UInt128 value = 0;
        foreach (var b in bytes)
        {
            value = (value << 8) | b;
        }

        return value;
    }

    private static Guid FromUInt128(UInt128 value)
    {
        Span<byte> bytes = stackalloc byte[16];
        for (var i = 15; i >= 0; i--)
        {
            bytes[i] = (byte)(value & 0xFF);
            value >>= 8;
        }

        return new Guid(bytes, bigEndian: true);
    }
}
