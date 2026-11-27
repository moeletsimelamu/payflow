using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Identity.Application.Passwords;

namespace PayFlow.UnitTests.Identity;

public sealed class CommonPasswordValidatorTests
{
    private static readonly PayFlowUser User = new()
    {
        Id = Guid.NewGuid(),
        Email = "thandi.nkosi@shop.example",
        DisplayName = "Thandi Nkosi",
    };

    [Fact]
    public void The_bundled_list_holds_the_12_plus_character_breached_passwords() =>
        CommonPasswordValidator.CommonCount.ShouldBe(46_146);

    [Theory]
    [InlineData("qwerty123456")]
    [InlineData("Qwerty123456")]      // case does not make it stronger
    [InlineData("  password1234  ")]  // nor do surrounding spaces
    [InlineData("1qaz2wsx3edc")]
    [InlineData("iloveyou1234")]
    public async Task Breached_passwords_are_refused(string password)
    {
        var result = await new CommonPasswordValidator().ValidateAsync(null!, User, password);
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(CommonPasswordValidator.TooCommonCode);
    }

    [Theory]
    [InlineData("my-payflow-account!")]
    [InlineData("PayFlowRocks2026")]
    [InlineData("thandi.nkosi-secret")]  // email name
    [InlineData("ThandiNkosi#horse")]   // display name
    public async Task Passwords_built_on_the_users_own_details_are_refused(string password)
    {
        var result = await new CommonPasswordValidator().ValidateAsync(null!, User, password);
        result.Succeeded.ShouldBeFalse();
        result.Errors.ShouldHaveSingleItem().Code.ShouldBe(CommonPasswordValidator.ContainsContextCode);
    }

    [Theory]
    [InlineData("correct horse battery staple")]
    [InlineData("member-passphrase-123")]
    [InlineData("tz8!Qm2#vLr0")]
    public async Task Uncommon_passwords_pass(string password) =>
        (await new CommonPasswordValidator().ValidateAsync(null!, User, password)).Succeeded.ShouldBeTrue();
}
