using System.Text.RegularExpressions;

namespace PayFlow.Infrastructure.Correlation;

/// <summary>Correlation id of the current unit of work (an HTTP request or a background job).</summary>
public interface ICorrelationContext
{
    string CorrelationId { get; }
}

/// <summary>Scoped, settable correlation context. Set by middleware for requests and by workers for jobs.</summary>
public sealed partial class CorrelationContext : ICorrelationContext
{
    public const string HeaderName = "X-Correlation-Id";

    public string CorrelationId { get; set; } = NewId();

    public static string NewId() => Guid.CreateVersion7().ToString("N");

    /// <summary>
    /// Client-supplied ids are accepted only if they are short and made of safe characters, so they
    /// cannot be used for log injection.
    /// </summary>
    public static bool IsValid(string? value) => value is not null && SafeId().IsMatch(value);

    [GeneratedRegex("^[A-Za-z0-9._:-]{8,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeId();
}
