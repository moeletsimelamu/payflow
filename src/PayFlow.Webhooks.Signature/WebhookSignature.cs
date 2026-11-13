using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace PayFlow.Webhooks.Signature;

public enum SignatureVerification
{
    Valid,
    MissingHeader,
    MalformedHeader,
    TimestampOutsideTolerance,
    SignatureMismatch,
}

/// <summary>
/// PayFlow webhook signatures (docs/PAYMENT_LIFECYCLE.md §9.3).
/// <code>
/// PayFlow-Signature: t={unix seconds},v1={signature}[,v1={signature with the previous secret}]
/// v1 = lowercase hex( HMAC-SHA256( key = UTF-8(secret), message = UTF-8($"{t}.{rawBody}") ) )
///
/// Example: secret "whsec_test_secret_123", t = 1731405600,
///          body {"id":"evt_1","type":"payment.succeeded"}
///       → v1 = c87b9fbc01cb589c867c7f34e13638fc8af978018c333066f38c906e709dc652
/// </code>
/// During a secret rotation the header carries one <c>v1</c> per active secret; a receiver accepts
/// the payload if any of them matches its secret. Receivers must verify against the raw request body,
/// reject timestamps outside the tolerance (replay protection) and de-duplicate on the event id.
/// </summary>
public static class WebhookSignature
{
    public const string HeaderName = "PayFlow-Signature";

    /// <summary>Maximum age (or clock skew) of a signed timestamp that receivers should accept.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string ComputeV1(string secret, long timestamp, string payload)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        ArgumentNullException.ThrowIfNull(payload);
        var message = Encoding.UTF8.GetBytes(string.Create(CultureInfo.InvariantCulture, $"{timestamp}.{payload}"));
        return Convert.ToHexStringLower(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), message));
    }

    /// <summary>Builds the header value, signing with every secret given (current first).</summary>
    public static string CreateHeader(long timestamp, string payload, IEnumerable<string> secrets)
    {
        ArgumentNullException.ThrowIfNull(secrets);
        var signatures = secrets.Select(s => "v1=" + ComputeV1(s, timestamp, payload)).ToList();
        if (signatures.Count == 0)
        {
            throw new ArgumentException("At least one secret is required.", nameof(secrets));
        }

        return string.Create(CultureInfo.InvariantCulture, $"t={timestamp},") + string.Join(",", signatures);
    }

    public static SignatureVerification Verify(string payload, string? header, string secret, DateTimeOffset now, TimeSpan? tolerance = null)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentException.ThrowIfNullOrEmpty(secret);
        if (string.IsNullOrWhiteSpace(header))
        {
            return SignatureVerification.MissingHeader;
        }

        long? timestamp = null;
        var candidates = new List<string>();
        foreach (var part in header.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = part.IndexOf('=', StringComparison.Ordinal);
            if (separator <= 0)
            {
                return SignatureVerification.MalformedHeader;
            }

            var key = part[..separator];
            var value = part[(separator + 1)..];
            if (key == "t" && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var t))
            {
                timestamp = t;
            }
            else if (key == "v1" && value.Length == 64)
            {
                candidates.Add(value);
            }
        }

        if (timestamp is null || candidates.Count == 0)
        {
            return SignatureVerification.MalformedHeader;
        }

        var age = now - DateTimeOffset.FromUnixTimeSeconds(timestamp.Value);
        if (age.Duration() > (tolerance ?? DefaultTolerance))
        {
            return SignatureVerification.TimestampOutsideTolerance;
        }

        var expected = Encoding.ASCII.GetBytes(ComputeV1(secret, timestamp.Value, payload));
        return candidates.Any(c => CryptographicOperations.FixedTimeEquals(expected, Encoding.ASCII.GetBytes(c.ToLowerInvariant())))
            ? SignatureVerification.Valid
            : SignatureVerification.SignatureMismatch;
    }
}
