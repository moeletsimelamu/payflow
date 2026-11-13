namespace PayFlow.SharedKernel.Security;

/// <summary>
/// Marks a property whose value must never appear in logs, audit diffs or error responses.
/// The logging and audit pipelines replace it with <see cref="Redaction.Mask"/>.
/// </summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class SensitiveAttribute : Attribute
{
}

/// <summary>Shared redaction rules for logs, audit records and webhook attempt headers.</summary>
public static class Redaction
{
    public const string Mask = "***REDACTED***";

    // Matched against the lower-cased name with '-' and '_' removed.
    private static readonly string[] SensitiveFragments =
    [
        "password", "secret", "token", "apikey", "authorization", "cookie", "signature", "pepper", "cvv", "cardnumber",
    ];

    /// <summary>True when a property, header or field name indicates a secret value.</summary>
    public static bool IsSensitiveName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        var normalised = name.Replace("-", string.Empty, StringComparison.Ordinal)
            .Replace("_", string.Empty, StringComparison.Ordinal)
            .ToLowerInvariant();
        return normalised == "pan" || SensitiveFragments.Any(f => normalised.Contains(f, StringComparison.Ordinal));
    }
}
