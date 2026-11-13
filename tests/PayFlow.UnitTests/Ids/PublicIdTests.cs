using PayFlow.SharedKernel.Ids;

namespace PayFlow.UnitTests.Ids;

public sealed class PublicIdTests
{
    [Fact]
    public void Round_trips_random_ids()
    {
        for (var i = 0; i < 1000; i++)
        {
            var id = Guid.NewGuid();
            var text = PublicId.Create(IdPrefixes.Payment, id);
            text.Length.ShouldBe(30);
            PublicId.TryParse(text, IdPrefixes.Payment, out var parsed).ShouldBeTrue();
            parsed.ShouldBe(id);
        }
    }

    [Theory]
    [InlineData("mer_00000000000000000000000000")]
    [InlineData("pay_0000000000000000000000000")]
    [InlineData("pay_8zzzzzzzzzzzzzzzzzzzzzzzzz")]
    [InlineData("pay_0000000000000000000000000u")]
    [InlineData(null)]
    public void Rejects_wrong_prefix_length_or_characters(string? value) =>
        PublicId.TryParse(value, IdPrefixes.Payment, out _).ShouldBeFalse();

    [Fact]
    public void Uuid_v7_ids_sort_in_creation_order()
    {
        var earlier = PublicId.Create("pay", Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(1)));
        var later = PublicId.Create("pay", Guid.CreateVersion7(DateTimeOffset.UnixEpoch.AddDays(2)));
        string.CompareOrdinal(earlier, later).ShouldBeLessThan(0);
    }
}
