using System.Collections.Frozen;
using Microsoft.AspNetCore.Identity;
using PayFlow.Infrastructure.Persistence;

namespace PayFlow.Modules.Identity.Application.Passwords;

/// <summary>
/// Refuses passwords that appear in public breach lists, or that contain the user's own email
/// name, display name or "PayFlow" (SECURITY.md, SEC-4; ASVS 5.0 V6: common and context-specific
/// passwords). Runs wherever Identity validates a password: invitation acceptance and user creation.
/// <para>
/// The list is the 46,146 entries of 12 or more characters (PayFlow's minimum) among the million most
/// common breached passwords (SecLists, MIT; see NOTICE.txt). Shorter entries could never be set
/// anyway. Comparison ignores case: "Qwerty123456" is as weak as "qwerty123456".
/// </para>
/// </summary>
internal sealed class CommonPasswordValidator : IPasswordValidator<PayFlowUser>
{
    public const string TooCommonCode = "PasswordTooCommon";
    public const string ContainsContextCode = "PasswordContainsContext";
    private const string ResourceName = "PayFlow.Identity.common-passwords.txt";
    private const int MinContextLength = 4;

    private static readonly Lazy<FrozenSet<string>> Common = new(Load);

    /// <summary>How many passwords the bundled list holds (for tests and diagnostics).</summary>
    internal static int CommonCount => Common.Value.Count;

    public Task<IdentityResult> ValidateAsync(UserManager<PayFlowUser> manager, PayFlowUser user, string? password)
    {
        if (string.IsNullOrEmpty(password))
        {
            return Task.FromResult(IdentityResult.Success); // the length rule reports this
        }

        var normalized = password.Trim().ToLowerInvariant();
        if (Common.Value.Contains(normalized))
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = TooCommonCode,
                Description = "This password appears in lists of breached passwords. Choose a different one.",
            }));
        }

        if (ContextWords(user).Any(word => normalized.Contains(word, StringComparison.Ordinal)))
        {
            return Task.FromResult(IdentityResult.Failed(new IdentityError
            {
                Code = ContainsContextCode,
                Description = "The password must not contain your email name, your display name or \"PayFlow\".",
            }));
        }

        return Task.FromResult(IdentityResult.Success);
    }

    /// <summary>Words an attacker targeting this user would try first.</summary>
    private static IEnumerable<string> ContextWords(PayFlowUser? user)
    {
        yield return "payflow";
        var local = user?.Email?.Split('@')[0].ToLowerInvariant();
        if (local is { Length: >= MinContextLength })
        {
            yield return local;
        }

        var name = new string((user?.DisplayName ?? string.Empty).Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        if (name.Length >= MinContextLength)
        {
            yield return name;
        }
    }

    private static FrozenSet<string> Load()
    {
        using var stream = typeof(CommonPasswordValidator).Assembly.GetManifestResourceStream(ResourceName)
            ?? throw new InvalidOperationException($"Embedded resource {ResourceName} is missing.");
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.Ordinal);
        while (reader.ReadLine() is { } line)
        {
            if (line.Length > 0)
            {
                set.Add(line);
            }
        }

        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
