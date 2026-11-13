using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using PayFlow.Modules.Identity.Application;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.SharedKernel.Ids;

namespace PayFlow.UnitTests.Identity;

public sealed class MerchantAccessHandlerTests
{
    private static readonly Guid MerchantA = Guid.CreateVersion7();
    private static readonly Guid MerchantB = Guid.CreateVersion7();

    [Fact]
    public async Task Member_with_required_role_is_allowed()
    {
        var user = new FakeUser(new MerchantMembershipInfo(MerchantA, MerchantRoles.MerchantAdmin));
        (await Authorise(user, MerchantA, MerchantRoles.MerchantAdmin)).ShouldBeTrue();
    }

    [Fact]
    public async Task Operator_cannot_pass_an_admin_requirement()
    {
        var user = new FakeUser(new MerchantMembershipInfo(MerchantA, MerchantRoles.MerchantOperator));
        (await Authorise(user, MerchantA, MerchantRoles.MerchantAdmin)).ShouldBeFalse();
    }

    [Fact]
    public async Task Membership_of_another_merchant_is_not_access()
    {
        var user = new FakeUser(new MerchantMembershipInfo(MerchantA, MerchantRoles.MerchantAdmin));
        (await Authorise(user, MerchantB, [.. MerchantRoles.All])).ShouldBeFalse();
    }

    [Fact]
    public async Task Platform_admin_is_allowed_for_any_merchant()
    {
        var user = new FakeUser { IsPlatformAdmin = true };
        (await Authorise(user, MerchantB, MerchantRoles.MerchantAdmin)).ShouldBeTrue();
    }

    [Theory]
    [InlineData("not-an-id")]
    [InlineData("pay_00000000000000000000000000")]
    [InlineData(null)]
    public async Task Malformed_or_missing_route_value_is_denied(string? routeValue)
    {
        var user = new FakeUser(new MerchantMembershipInfo(MerchantA, MerchantRoles.MerchantAdmin));
        (await Authorise(user, routeValue, MerchantRoles.MerchantAdmin)).ShouldBeFalse();
    }

    [Fact]
    public async Task Anonymous_user_is_denied()
    {
        var user = new FakeUser { IsAuthenticated = false, IsPlatformAdmin = true };
        (await Authorise(user, MerchantA, MerchantRoles.MerchantAdmin)).ShouldBeFalse();
    }

    private static Task<bool> Authorise(ICurrentUser user, Guid merchantId, params string[] roles) =>
        Authorise(user, PublicId.Create(IdPrefixes.Merchant, merchantId), roles);

    private static async Task<bool> Authorise(ICurrentUser user, string? routeValue, params string[] roles)
    {
        var http = new DefaultHttpContext();
        if (routeValue is not null)
        {
            http.Request.RouteValues[MerchantAccessRequirement.RouteValueName] = routeValue;
        }

        var requirement = new MerchantAccessRequirement(roles);
        var context = new AuthorizationHandlerContext([requirement], new ClaimsPrincipal(), http);
        await new MerchantAccessHandler(user).HandleAsync(context);
        return context.HasSucceeded;
    }

    private sealed class FakeUser(params MerchantMembershipInfo[] memberships) : ICurrentUser
    {
        public bool IsAuthenticated { get; init; } = true;

        public Guid? UserId { get; } = Guid.CreateVersion7();

        public bool IsPlatformAdmin { get; init; }

        public IReadOnlyList<MerchantMembershipInfo> Memberships { get; } = memberships;

        public bool HasMerchantRole(Guid merchantId, params string[] roles) =>
            Memberships.Any(m => m.MerchantId == merchantId && (roles.Length == 0 || roles.Contains(m.Role)));
    }
}
