using System.Text;
using PayFlow.Modules.Identity.Application;

namespace PayFlow.UnitTests.Identity;

public sealed class TotpTests
{
    // RFC 6238 Appendix B, HMAC-SHA1 with the ASCII seed "12345678901234567890", 8 digits.
    private static readonly byte[] RfcSecret = Encoding.ASCII.GetBytes("12345678901234567890");

    [Theory]
    [InlineData(59L, "94287082")]
    [InlineData(1111111109L, "07081804")]
    [InlineData(1111111111L, "14050471")]
    [InlineData(1234567890L, "89005924")]
    [InlineData(2000000000L, "69279037")]
    [InlineData(20000000000L, "65353130")]
    public void Codes_match_the_rfc_6238_test_vectors(long unixSeconds, string expected) =>
        Totp.Code(RfcSecret, Totp.StepAt(DateTimeOffset.FromUnixTimeSeconds(unixSeconds)), digits: 8).ShouldBe(expected);

    [Fact]
    public void A_code_is_accepted_within_one_step_of_drift_and_only_after_the_last_used_step()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        var step = Totp.StepAt(now);

        Totp.Match(secret, Totp.Code(secret, step), now, lastUsedStep: 0).ShouldBe(step);
        Totp.Match(secret, Totp.Code(secret, step - 1), now, 0).ShouldBe(step - 1); // phone slightly behind
        Totp.Match(secret, Totp.Code(secret, step + 1), now, 0).ShouldBe(step + 1); // phone slightly ahead
        Totp.Match(secret, Totp.Code(secret, step - 2), now, 0).ShouldBeNull();
        Totp.Match(secret, Totp.Code(secret, step + 2), now, 0).ShouldBeNull();

        // Replay: a code for a step at or before the last accepted one is refused.
        Totp.Match(secret, Totp.Code(secret, step), now, lastUsedStep: step).ShouldBeNull();
        Totp.Match(secret, Totp.Code(secret, step - 1), now, lastUsedStep: step).ShouldBeNull();
    }

    [Theory]
    [InlineData("")]
    [InlineData("12345")]
    [InlineData("1234567")]
    [InlineData("abcdef")]
    public void Malformed_codes_never_match(string code) =>
        Totp.Match(Totp.NewSecret(), code, DateTimeOffset.UtcNow, 0).ShouldBeNull();

    [Fact]
    public void Spaces_in_a_typed_code_are_ignored()
    {
        var secret = Totp.NewSecret();
        var now = DateTimeOffset.UtcNow;
        var code = Totp.Code(secret, Totp.StepAt(now));
        Totp.Match(secret, $"{code[..3]} {code[3..]}", now, 0).ShouldNotBeNull();
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("f", "MY")]
    [InlineData("fo", "MZXQ")]
    [InlineData("foo", "MZXW6")]
    [InlineData("foob", "MZXW6YQ")]
    [InlineData("fooba", "MZXW6YTB")]
    [InlineData("foobar", "MZXW6YTBOI")]
    public void Base32_matches_the_rfc_4648_vectors(string text, string encoded)
    {
        Base32.Encode(Encoding.ASCII.GetBytes(text)).ShouldBe(encoded);
        if (text.Length > 0)
        {
            Base32.Decode(encoded.ToLowerInvariant())!.ShouldBe(Encoding.ASCII.GetBytes(text));
        }
    }

    [Fact]
    public void Base32_rejects_characters_outside_the_alphabet() => Base32.Decode("MZXW6YT1").ShouldBeNull();

    [Fact]
    public void The_provisioning_uri_carries_the_secret_and_parameters()
    {
        var secret = Encoding.ASCII.GetBytes("12345678901234567890");
        Totp.ProvisioningUri("PayFlow", "admin@payflow.test", secret).ShouldBe(
            "otpauth://totp/PayFlow:admin%40payflow.test?secret=GEZDGNBVGY3TQOJQGEZDGNBVGY3TQOJQ&issuer=PayFlow&algorithm=SHA1&digits=6&period=30");
    }

    [Fact]
    public void Recovery_codes_are_four_groups_of_five_and_normalise_for_comparison()
    {
        var code = MfaService.NewRecoveryCode();
        code.ShouldMatch("^[a-z2-7]{5}(-[a-z2-7]{5}){3}$");
        MfaService.NormalizeRecoveryCode($" {code.ToUpperInvariant().Replace('-', ' ')} ").ShouldBe(code.Replace("-", "").ToUpperInvariant());
    }
}
