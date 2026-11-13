using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Merchants.Application;

public sealed class ApiKeySecurityOptions
{
    public const string Section = "Security:ApiKeys";

    /// <summary>Base64 server secret (at least 32 bytes) mixed into every key hash. Supplied via secrets only.</summary>
    [Sensitive]
    public string? Pepper { get; set; }

    public TimeSpan DefaultRotationGracePeriod { get; set; } = TimeSpan.FromHours(24);

    public TimeSpan MaxRotationGracePeriod { get; set; } = TimeSpan.FromDays(7);

    public int MaxActiveKeysPerMerchant { get; set; } = 10;

    public TimeSpan CacheDuration { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Generates and verifies merchant API keys of the form <c>pf_test_sk_{prefix8}_{secret52}</c>.
/// <para>
/// The secret carries 256 random bits, so a fast keyed hash (HMAC-SHA256 with a server pepper) is
/// sufficient: brute force is infeasible and lookups stay cheap. Only the prefix and the HMAC are
/// stored; comparison is constant-time. A leaked database alone does not allow forging keys.
/// </para>
/// </summary>
public sealed class ApiKeyCredentials
{
    public const int PrefixLength = 8;
    public const int SecretLength = 52; // 32 bytes in base32
    private const string Alphabet = "0123456789abcdefghjkmnpqrstvwxyz";

    private readonly byte[] _pepper;

    public ApiKeyCredentials(IOptions<ApiKeySecurityOptions> options, IHostEnvironment environment, ILogger<ApiKeyCredentials> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(environment);
        var configured = options.Value.Pepper;
        if (!string.IsNullOrEmpty(configured))
        {
            _pepper = Convert.FromBase64String(configured);
            if (_pepper.Length < 32)
            {
                throw new InvalidOperationException($"{ApiKeySecurityOptions.Section}:Pepper must decode to at least 32 bytes.");
            }
        }
        else if (environment.IsDevelopment())
        {
            logger.LogWarning("No API key pepper configured; using an ephemeral one. API keys will stop working when the process restarts.");
            _pepper = RandomNumberGenerator.GetBytes(32);
        }
        else
        {
            throw new InvalidOperationException(
                $"{ApiKeySecurityOptions.Section}:Pepper is required. Generate one with: openssl rand -base64 32");
        }
    }

    public static int FullLength => MerchantApiAuth.KeyPrefix.Length + PrefixLength + 1 + SecretLength;

    /// <summary>Creates a new key. The returned full key must be shown to the user once and then discarded.</summary>
    public (string FullKey, string Prefix, byte[] Hash) Generate()
    {
        var prefix = Encode(RandomNumberGenerator.GetBytes(5)); // 40 bits → 8 chars
        var secret = Encode(RandomNumberGenerator.GetBytes(32)); // 256 bits → 52 chars
        var fullKey = string.Concat(MerchantApiAuth.KeyPrefix, prefix, "_", secret);
        return (fullKey, prefix, Hash(fullKey));
    }

    public static bool TryParsePrefix(string? token, out string prefix)
    {
        prefix = string.Empty;
        if (token is null || token.Length != FullLength || !token.StartsWith(MerchantApiAuth.KeyPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = token.AsSpan(MerchantApiAuth.KeyPrefix.Length);
        if (body[PrefixLength] != '_')
        {
            return false;
        }

        foreach (var c in body[..PrefixLength])
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        foreach (var c in body[(PrefixLength + 1)..])
        {
            if (!Alphabet.Contains(c, StringComparison.Ordinal))
            {
                return false;
            }
        }

        prefix = body[..PrefixLength].ToString();
        return true;
    }

    public byte[] Hash(string fullKey) => HMACSHA256.HashData(_pepper, Encoding.UTF8.GetBytes(fullKey));

    public bool Verify(string fullKey, byte[] expectedHash) => CryptographicOperations.FixedTimeEquals(Hash(fullKey), expectedHash);

    /// <summary>Base32 (Crockford alphabet, lower case), most significant bit first.</summary>
    private static string Encode(byte[] bytes)
    {
        var output = new StringBuilder((bytes.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in bytes)
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
}
