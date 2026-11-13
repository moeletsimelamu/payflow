using System.Net;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Platform;

public sealed class PlatformTests(PayFlowTestHost host)
{
    [Fact]
    public async Task Liveness_and_readiness_report_healthy()
    {
        using var client = host.CreateDashboardClient();

        (await client.Http.GetAsync(new Uri("/health/live", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.OK);
        var ready = await client.Http.GetAsync(new Uri("/health/ready", UriKind.Relative));

        ready.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await ready.Content.ReadAsStringAsync()).ShouldBe("Healthy");
    }

    [Fact]
    public async Task Valid_correlation_ids_are_echoed_and_unsafe_ones_replaced()
    {
        using var client = host.CreateDashboardClient();

        using var valid = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        valid.Headers.Add("X-Correlation-Id", "client-supplied-123");
        var echoed = await client.Http.SendAsync(valid);
        echoed.Headers.GetValues("X-Correlation-Id").Single().ShouldBe("client-supplied-123");

        using var unsafeRequest = new HttpRequestMessage(HttpMethod.Get, "/health/live");
        unsafeRequest.Headers.TryAddWithoutValidation("X-Correlation-Id", "<script>alert(1)</script>");
        var replaced = await client.Http.SendAsync(unsafeRequest);
        var id = replaced.Headers.GetValues("X-Correlation-Id").Single();
        id.ShouldNotContain("<");
        id.Length.ShouldBe(32);
    }

    [Fact]
    public async Task Responses_carry_defensive_headers_and_are_never_cached()
    {
        using var client = host.CreateDashboardClient();

        var response = await client.Http.GetAsync(new Uri("/api/auth/me", UriKind.Relative));

        response.Headers.GetValues("X-Content-Type-Options").Single().ShouldBe("nosniff");
        response.Headers.GetValues("X-Frame-Options").Single().ShouldBe("DENY");
        response.Headers.CacheControl!.NoStore.ShouldBeTrue();
    }

    [Fact]
    public async Task Unknown_routes_return_problem_details_with_a_correlation_id()
    {
        using var client = host.CreateDashboardClient();

        var response = await client.Http.GetAsync(new Uri("/api/does-not-exist", UriKind.Relative));

        response.StatusCode.ShouldBe(HttpStatusCode.NotFound);
        response.Content.Headers.ContentType!.MediaType.ShouldBe("application/problem+json");
        (await DashboardClient.ReadJsonAsync(response)).GetProperty("correlationId").GetString().ShouldNotBeNullOrEmpty();
    }

    [Fact]
    public void Every_api_endpoint_declares_its_authorization()
    {
        var endpoints = host.Factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()
            .Where(e => e.RoutePattern.RawText is { } route &&
                        (route.StartsWith("/api", StringComparison.Ordinal) || route.StartsWith("/v1", StringComparison.Ordinal)))
            .ToList();

        endpoints.ShouldNotBeEmpty();
        endpoints
            .Where(e => e.Metadata.GetMetadata<IAuthorizeData>() is null && e.Metadata.GetMetadata<IAllowAnonymous>() is null)
            .Select(e => e.RoutePattern.RawText)
            .ShouldBeEmpty();
    }

    [Fact]
    public async Task Supported_currencies_are_seeded_with_their_minor_units()
    {
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var command = new NpgsqlCommand("SELECT code, minor_units FROM platform.currencies ORDER BY code", db);
        await using var reader = await command.ExecuteReaderAsync();
        var currencies = new Dictionary<string, int>();
        while (await reader.ReadAsync())
        {
            currencies[reader.GetString(0)] = reader.GetInt32(1);
        }

        currencies.ShouldBe(new Dictionary<string, int>
        {
            ["EUR"] = 2, ["GBP"] = 2, ["JPY"] = 0, ["KWD"] = 3, ["USD"] = 2, ["ZAR"] = 2,
        }, ignoreOrder: true);
    }
}
