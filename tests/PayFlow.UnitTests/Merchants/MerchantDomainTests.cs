using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayFlow.Modules.Merchants.Application;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;
using PayFlow.SharedKernel.Money;

namespace PayFlow.UnitTests.Merchants;

public sealed class MerchantDomainTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 12, 10, 0, 0, TimeSpan.Zero);

    public static TheoryData<MerchantStatus, MerchantStatus, bool> Transitions()
    {
        var allowed = new HashSet<(MerchantStatus, MerchantStatus)>
        {
            (MerchantStatus.PendingActivation, MerchantStatus.Active),
            (MerchantStatus.PendingActivation, MerchantStatus.Closed),
            (MerchantStatus.Active, MerchantStatus.Suspended),
            (MerchantStatus.Active, MerchantStatus.Closed),
            (MerchantStatus.Suspended, MerchantStatus.Active),
            (MerchantStatus.Suspended, MerchantStatus.Closed),
        };
        var data = new TheoryData<MerchantStatus, MerchantStatus, bool>();
        foreach (var from in Enum.GetValues<MerchantStatus>())
        {
            foreach (var to in Enum.GetValues<MerchantStatus>())
            {
                data.Add(from, to, allowed.Contains((from, to)));
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Transitions))]
    public void Every_status_pair_follows_the_whitelist(MerchantStatus from, MerchantStatus to, bool allowed) =>
        Merchant.CanTransition(from, to).ShouldBe(allowed);

    [Fact]
    public void Suspension_requires_a_reason()
    {
        var merchant = NewMerchant();
        merchant.ChangeStatus(MerchantStatus.Active, null, Now).IsSuccess.ShouldBeTrue();

        var result = merchant.ChangeStatus(MerchantStatus.Suspended, " ", Now);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe("reason_required");
        merchant.Status.ShouldBe(MerchantStatus.Active);
    }

    [Fact]
    public void Currency_limits_respect_precision_and_the_fee_plan_minimum()
    {
        var merchant = NewMerchant();
        var plan = FeePlan.StandardV1(Now);

        merchant.ConfigureCurrency(Currency.ZAR, 10.005m, null, plan, Now).Error!.Code.ShouldBe("amount_precision_exceeded");
        merchant.ConfigureCurrency(Currency.ZAR, 4.99m, null, plan, Now).Error!.Code.ShouldBe("max_below_minimum");
        merchant.ConfigureCurrency(Currency.JPY, 50_000m, 1_000m, plan, Now).IsSuccess.ShouldBeTrue();
        merchant.Currencies.Single().Currency.ShouldBe("JPY");
    }

    [Fact]
    public void Closed_merchants_cannot_be_changed()
    {
        var merchant = NewMerchant();
        merchant.ChangeStatus(MerchantStatus.Closed, "Onboarding abandoned", Now).IsSuccess.ShouldBeTrue();

        merchant.UpdateProfile("New name", "ops@example.test", Now).Error!.Code.ShouldBe("merchant_closed");
        merchant.ChangeStatus(MerchantStatus.Active, null, Now).Error!.Code.ShouldBe("invalid_status_transition");
    }

    [Theory]
    [InlineData("1.0", "ZAR", "2.00", "5.00", "invalid_rate")]
    [InlineData("0.0000001", "ZAR", "2.00", "5.00", "invalid_rate")]
    [InlineData("0.029", "ZAR", "2.00", "2.00", "fee_exceeds_minimum")]
    [InlineData("0.5", "ZAR", "2.00", "4.00", "fee_exceeds_minimum")]
    [InlineData("0.029", "ZAR", "2.001", "5.00", "amount_precision_exceeded")]
    public void Fee_plans_reject_terms_that_could_consume_a_payment(string rate, string currency, string fixedFee, string minimum, string expectedCode)
    {
        var ccy = Currency.FromCode(currency);
        var result = FeePlan.Create(Guid.CreateVersion7(), "Test", 1, Parse(rate), [(ccy, Parse(fixedFee), Parse(minimum))], Now);

        result.IsSuccess.ShouldBeFalse();
        result.Error!.Code.ShouldBe(expectedCode);
    }

    [Fact]
    public void Api_key_states_follow_revocation_and_expiry()
    {
        var key = ApiKey.Issue(Guid.CreateVersion7(), Guid.CreateVersion7(), "Server", "abcd1234", new byte[32], "usr_x", Now);
        key.StateAt(Now).ShouldBe(ApiKeyState.Active);

        key.ReplaceWith(Guid.CreateVersion7(), Now.AddHours(1));
        key.StateAt(Now).ShouldBe(ApiKeyState.Expiring);
        key.IsUsableAt(Now.AddMinutes(59)).ShouldBeTrue();
        key.StateAt(Now.AddHours(1)).ShouldBe(ApiKeyState.Expired);

        key.Revoke(Now);
        key.StateAt(Now).ShouldBe(ApiKeyState.Revoked);
        key.IsUsableAt(Now).ShouldBeFalse();
    }

    [Fact]
    public void Generated_api_keys_parse_and_verify_only_with_the_same_pepper()
    {
        var credentials = Credentials(Convert.ToBase64String(new byte[32]));
        var (fullKey, prefix, hash) = credentials.Generate();

        fullKey.ShouldStartWith(MerchantApiAuth.KeyPrefix + prefix + "_");
        fullKey.Length.ShouldBe(ApiKeyCredentials.FullLength);
        hash.Length.ShouldBe(32);
        ApiKeyCredentials.TryParsePrefix(fullKey, out var parsed).ShouldBeTrue();
        parsed.ShouldBe(prefix);
        credentials.Verify(fullKey, hash).ShouldBeTrue();
        credentials.Verify(fullKey[..^1] + (fullKey[^1] == 'a' ? 'b' : 'a'), hash).ShouldBeFalse();

        var otherPepper = Credentials(Convert.ToBase64String(Enumerable.Repeat((byte)7, 32).ToArray()));
        otherPepper.Verify(fullKey, hash).ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("pf_live_sk_abcd1234_0000000000000000000000000000000000000000000000000000")]
    [InlineData("pf_test_sk_ABCD1234_0000000000000000000000000000000000000000000000000000")]
    [InlineData("pf_test_sk_abcd1234-0000000000000000000000000000000000000000000000000000")]
    [InlineData("pf_test_sk_abcd1234_000")]
    public void Malformed_keys_are_rejected_before_any_lookup(string token) =>
        ApiKeyCredentials.TryParsePrefix(token, out _).ShouldBeFalse();

    [Fact]
    public void A_missing_pepper_outside_development_fails_fast() =>
        Should.Throw<InvalidOperationException>(() => Credentials(null, Environments.Production));

    private static Merchant NewMerchant() =>
        Merchant.Register(Guid.CreateVersion7(), "Acme (Pty) Ltd", "Acme", "ops@acme.test", "za", FeePlan.StandardV1Id, Now);

    private static decimal Parse(string value) => decimal.Parse(value, System.Globalization.CultureInfo.InvariantCulture);

    private static ApiKeyCredentials Credentials(string? pepper, string environment = "Testing") => new(
        Options.Create(new ApiKeySecurityOptions { Pepper = pepper }),
        new FakeEnvironment(environment),
        NullLogger<ApiKeyCredentials>.Instance);

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "PayFlow.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } =
            new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
