using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PayFlow.Modules.Identity.Application;

/// <summary>
/// Time-based one-time passwords (RFC 6238 over RFC 4226 HOTP): HMAC-SHA1, 30-second steps, six digits,
/// the parameters every authenticator app supports. Verified against the RFC 6238 test vectors.
/// </summary>
internal static class Totp
{
    public const int Digits = 6;
    public const int SecretBytes = 20; // 160 bits, the RFC 4226 recommendation for HMAC-SHA1
    public static readonly TimeSpan Step = TimeSpan.FromSeconds(30);

    /// <summary>Steps either side of now that are still accepted, for clock drift between phone and server.</summary>
    public const int AllowedDrift = 1;

    public static byte[] NewSecret() => RandomNumberGenerator.GetBytes(SecretBytes);

    public static long StepAt(DateTimeOffset time) => time.ToUnixTimeSeconds() / (long)Step.TotalSeconds;

    /// <summary>The code for one time step (<paramref name="digits"/> long, zero-padded).</summary>
    public static string Code(byte[] secret, long step, int digits = Digits)
    {
        ArgumentNullException.ThrowIfNull(secret);
        Span<byte> counter = stackalloc byte[8];
        System.Buffers.Binary.BinaryPrimitives.WriteInt64BigEndian(counter, step);
        Span<byte> hash = stackalloc byte[HMACSHA1.HashSizeInBytes];
#pragma warning disable CA5350 // HMAC-SHA1 is what RFC 6238 and authenticator apps use; HMAC is not affected by SHA-1 collisions.
        HMACSHA1.HashData(secret, counter, hash);
#pragma warning restore CA5350
        var offset = hash[^1] & 0x0F;
        var binary = ((hash[offset] & 0x7F) << 24) | (hash[offset + 1] << 16) | (hash[offset + 2] << 8) | hash[offset + 3];
        var modulo = (int)Math.Pow(10, digits);
        return (binary % modulo).ToString(CultureInfo.InvariantCulture).PadLeft(digits, '0');
    }

    /// <summary>
    /// The step whose code matches <paramref name="code"/> within the allowed drift and is later than
    /// <paramref name="lastUsedStep"/> (a code is accepted once only), or null.
    /// </summary>
    public static long? Match(byte[] secret, string code, DateTimeOffset now, long lastUsedStep)
    {
        var normalized = Normalize(code);
        if (normalized.Length != Digits)
        {
            return null;
        }

        var current = StepAt(now);
        for (var step = current - AllowedDrift; step <= current + AllowedDrift; step++)
        {
            if (step > lastUsedStep && CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(Code(secret, step)), Encoding.ASCII.GetBytes(normalized)))
            {
                return step;
            }
        }

        return null;
    }

    /// <summary>The otpauth:// URI authenticator apps import (as a QR code or a link).</summary>
    public static string ProvisioningUri(string issuer, string account, byte[] secret) =>
        string.Create(CultureInfo.InvariantCulture,
            $"otpauth://totp/{Uri.EscapeDataString(issuer)}:{Uri.EscapeDataString(account)}?secret={Base32.Encode(secret)}&issuer={Uri.EscapeDataString(issuer)}&algorithm=SHA1&digits={Digits}&period={(int)Step.TotalSeconds}");

    private static string Normalize(string? code) =>
        new((code ?? string.Empty).Where(char.IsAsciiDigit).ToArray());
}

/// <summary>RFC 4648 Base32 without padding: the encoding authenticator apps expect for secrets.</summary>
internal static class Base32
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Encode(ReadOnlySpan<byte> data)
    {
        var output = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                output.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }

        if (bits > 0)
        {
            output.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return output.ToString();
    }

    /// <summary>Decodes, ignoring spaces, hyphens, padding and case; null if anything else is present.</summary>
    public static byte[]? Decode(string? text)
    {
        var output = new List<byte>();
        int buffer = 0, bits = 0;
        foreach (var c in text ?? string.Empty)
        {
            if (c is ' ' or '-' or '=')
            {
                continue;
            }

            var value = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xFF));
                bits -= 8;
            }
        }

        return output.Count == 0 ? null : [.. output];
    }
}
