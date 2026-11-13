using System.Net;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PayFlow.Infrastructure.Outbox;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Webhooks.Application;
using PayFlow.Webhooks.Signature;

namespace PayFlow.IntegrationTests.Webhooks;

/// <summary>
/// All webhook tests live in one class, so xUnit runs them one after another: the dispatcher and
/// sender work on the shared database. Each test sends to its own unique host and only looks at
/// what that host received.
/// </summary>
public sealed class WebhookTests(PayFlowTestHost host)
{
    private sealed record Harness(PayFlowApiFactory Factory, FakeTimeProvider Clock, StubWebhookReceiver Receiver, PayingMerchant Merchant) : IAsyncDisposable
    {
        public async ValueTask DisposeAsync()
        {
            Merchant.Dispose();
            await Factory.DisposeAsync();
        }
    }

    private async Task<Harness> HarnessAsync()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var receiver = new StubWebhookReceiver();
        var factory = host.CreateFactory(clock: clock, configureServices: receiver.Register);
        var merchant = await PaymentScenario.PayingMerchantAsync(host, factory);
        return new Harness(factory, clock, receiver, merchant);
    }

    private static async Task<(string Id, string Secret)> CreateEndpointAsync(Harness h, string url, params string[] eventTypes)
    {
        var response = await h.Merchant.Owner.PostAsync($"/api/merchants/{h.Merchant.Id}/webhook-endpoints", new { url, description = "test", eventTypes = eventTypes.Length == 0 ? ["*"] : eventTypes });
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        var body = await DashboardClient.ReadJsonAsync(response);
        return (body.GetProperty("endpoint").GetProperty("id").GetString()!, body.GetProperty("secret").GetString()!);
    }

    private static async Task<JsonElement> DeliveriesAsync(Harness h) =>
        await DashboardClient.ReadJsonAsync(await h.Merchant.Owner.Http.GetAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-deliveries", UriKind.Relative)));

    private static Task<int> DispatchAsync(Harness h) => RefundScenario.RunJobAsync<OutboxDispatchJob>(h.Factory);

    private static Task<int> SendAsync(Harness h) => RefundScenario.RunJobAsync<WebhookDeliveryJob>(h.Factory);

    [Fact]
    public async Task Endpoints_show_their_secret_once_and_store_it_encrypted()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();

        var (endpointId, secret) = await CreateEndpointAsync(h, $"https://{hostName}/hooks");

        secret.ShouldStartWith("whsec_");
        var list = await h.Merchant.Owner.Http.GetStringAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-endpoints", UriKind.Relative));
        list.ShouldContain(endpointId);
        list.ShouldContain(secret[^4..]);
        list.ShouldNotContain(secret);
        var stored = await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM webhooks.endpoints WHERE secret_ciphertext LIKE 'v1.%' AND secret_ciphertext NOT LIKE '%' || @s || '%'", ("s", secret));
        stored.ShouldBeGreaterThanOrEqualTo(1);
        h.Factory.Logs.Contains(secret).ShouldBeFalse();
    }

    [Theory]
    [InlineData("http://shop.example.com/hooks")]
    [InlineData("https://localhost/hooks")]
    [InlineData("https://10.0.0.7/hooks")]
    [InlineData("https://169.254.169.254/latest/meta-data")]
    [InlineData("https://user:pw@shop.example.com/hooks")]
    public async Task Unsafe_endpoint_urls_are_refused(string url)
    {
        await using var h = await HarnessAsync();

        var response = await h.Merchant.Owner.PostAsync($"/api/merchants/{h.Merchant.Id}/webhook-endpoints", new { url, eventTypes = new[] { "*" } });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DashboardClient.ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("webhook_url_not_allowed");
    }

    [Fact]
    public async Task A_payment_becomes_one_signed_event_delivered_to_the_endpoint()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();
        var (_, secret) = await CreateEndpointAsync(h, $"https://{hostName}/hooks");
        var paymentId = await RefundScenario.PaidAsync(h.Merchant.Api, "100.00");

        await DispatchAsync(h);
        await SendAsync(h);

        var received = h.Receiver.For(hostName).ShouldHaveSingleItem();
        WebhookSignature.Verify(received.Body, received.Headers[WebhookSignature.HeaderName], secret, h.Clock.GetUtcNow()).ShouldBe(SignatureVerification.Valid);
        received.Headers["PayFlow-Event-Type"].ShouldBe("payment.succeeded");
        received.Headers["PayFlow-Delivery-Attempt"].ShouldBe("1");
        using var body = JsonDocument.Parse(received.Body);
        body.RootElement.GetProperty("id").GetString().ShouldBe(received.Headers["PayFlow-Event-Id"]);
        body.RootElement.GetProperty("data").GetProperty("paymentId").GetString().ShouldBe(paymentId);
        body.RootElement.GetProperty("simulated").GetBoolean().ShouldBeTrue();

        var delivery = (await DeliveriesAsync(h)).GetProperty("items").EnumerateArray().Single();
        delivery.GetProperty("status").GetString().ShouldBe("Succeeded");
        var detail = await DashboardClient.ReadJsonAsync(await h.Merchant.Owner.Http.GetAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-deliveries/{delivery.GetProperty("id").GetString()}", UriKind.Relative)));
        var attempt = detail.GetProperty("attempts").EnumerateArray().Single();
        attempt.GetProperty("responseStatus").GetInt32().ShouldBe(200);
        attempt.GetProperty("requestHeaders").GetProperty("PayFlow-Signature").GetString()!.ShouldEndWith("v1=***");

        var events = await MerchantScenario.ReadAsync(await h.Merchant.Api.GetAsync(new Uri("/v1/events?type=payment.succeeded", UriKind.Relative)));
        events.GetProperty("data").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(received.Headers["PayFlow-Event-Id"]);
    }

    [Fact]
    public async Task Failed_deliveries_retry_on_the_documented_schedule()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();
        await CreateEndpointAsync(h, $"https://{hostName}/hooks");
        h.Receiver.Script(hostName, HttpStatusCode.InternalServerError, HttpStatusCode.ServiceUnavailable, HttpStatusCode.OK);
        await RefundScenario.PaidAsync(h.Merchant.Api, "50.00");
        await DispatchAsync(h);

        var start = h.Clock.GetUtcNow();
        await SendAsync(h); // attempt 1: 500
        var afterFirst = (await DeliveriesAsync(h)).GetProperty("items").EnumerateArray().Single();
        afterFirst.GetProperty("status").GetString().ShouldBe("Retrying");
        var next = afterFirst.GetProperty("nextAttemptAt").GetDateTimeOffset();
        (next - start).ShouldBeInRange(TimeSpan.FromSeconds(48), TimeSpan.FromSeconds(72)); // 1 min ± 20 %

        await SendAsync(h);
        h.Receiver.For(hostName).Count.ShouldBe(1); // not due yet

        h.Clock.Advance(TimeSpan.FromMinutes(2));
        await SendAsync(h); // attempt 2: 503, next in 5 min ± 20 %
        h.Clock.Advance(TimeSpan.FromMinutes(7));
        await SendAsync(h); // attempt 3: 200

        h.Receiver.For(hostName).Select(r => r.Headers["PayFlow-Delivery-Attempt"]).ShouldBe(["1", "2", "3"]);
        h.Receiver.For(hostName).Select(r => r.Body).Distinct().ShouldHaveSingleItem(); // identical payload on every attempt
        var final = (await DeliveriesAsync(h)).GetProperty("items").EnumerateArray().Single();
        final.GetProperty("status").GetString().ShouldBe("Succeeded");
        final.GetProperty("attemptCount").GetInt32().ShouldBe(3);
    }

    [Fact]
    public async Task Deliveries_are_exhausted_after_eight_attempts_and_can_be_redelivered()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();
        await CreateEndpointAsync(h, $"https://{hostName}/hooks");
        h.Receiver.Default(hostName, HttpStatusCode.BadGateway);
        await RefundScenario.PaidAsync(h.Merchant.Api, "50.00");
        await DispatchAsync(h);

        for (var i = 0; i < 8; i++)
        {
            await SendAsync(h);
            h.Clock.Advance(TimeSpan.FromHours(13)); // longer than any delay plus jitter
        }

        h.Receiver.For(hostName).Count.ShouldBe(8);
        await SendAsync(h);
        h.Receiver.For(hostName).Count.ShouldBe(8); // exhausted: no ninth automatic attempt

        // The session cookie expired while the clock jumped ahead; sign in again.
        using var owner = await MerchantScenario.LoginAsync(host, h.Merchant.Merchant.AdminEmail, h.Merchant.Merchant.AdminPassword, h.Factory);
        var delivery = (await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-deliveries", UriKind.Relative))))
            .GetProperty("items").EnumerateArray().Single();
        delivery.GetProperty("status").GetString().ShouldBe("Exhausted");

        h.Receiver.Default(hostName, HttpStatusCode.OK);
        var redeliver = await owner.PostAsync($"/api/merchants/{h.Merchant.Id}/webhook-deliveries/{delivery.GetProperty("id").GetString()}/redeliver", null);
        redeliver.StatusCode.ShouldBe(HttpStatusCode.OK);
        await SendAsync(h);

        h.Receiver.For(hostName)[^1].Headers["PayFlow-Delivery-Attempt"].ShouldBe("9");
        var final = (await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-deliveries", UriKind.Relative))))
            .GetProperty("items").EnumerateArray().Single();
        final.GetProperty("status").GetString().ShouldBe("Succeeded");
    }

    [Fact]
    public async Task Two_concurrent_dispatchers_create_each_event_exactly_once()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();
        await CreateEndpointAsync(h, $"https://{hostName}/hooks");
        for (var i = 0; i < 10; i++)
        {
            await RefundScenario.PaidAsync(h.Merchant.Api, "20.00");
        }

        await Task.WhenAll(DispatchAsync(h), DispatchAsync(h));
        await DispatchAsync(h); // nothing left to do

        var merchant = PaymentScenario.DecodeMerchant(h.Merchant.Id);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM webhooks.events WHERE merchant_id = @m", ("m", merchant))).ShouldBe(10);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM webhooks.deliveries WHERE merchant_id = @m", ("m", merchant))).ShouldBe(10);
        (await PaymentScenario.ScalarAsync(host,
            """
            SELECT count(*) FROM platform.outbox_messages m
             WHERE m.payload->>'merchantId' = @mid
               AND (m.processed_at IS NULL OR (SELECT count(*) FROM webhooks.events e WHERE e.source_message_id = m.id) <> 1)
            """, ("mid", h.Merchant.Id))).ShouldBe(0);
    }

    [Fact]
    public async Task During_a_secret_roll_deliveries_are_signed_with_both_secrets()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();
        var (endpointId, oldSecret) = await CreateEndpointAsync(h, $"https://{hostName}/hooks");

        var roll = await DashboardClient.ReadJsonAsync(await h.Merchant.Owner.PostAsync($"/api/merchants/{h.Merchant.Id}/webhook-endpoints/{endpointId}/roll-secret", null));
        var newSecret = roll.GetProperty("secret").GetString()!;
        (await h.Merchant.Owner.PostAsync($"/api/merchants/{h.Merchant.Id}/webhook-endpoints/{endpointId}/ping", null)).StatusCode.ShouldBe(HttpStatusCode.Accepted);
        await SendAsync(h);

        var ping = h.Receiver.For(hostName).ShouldHaveSingleItem();
        ping.Headers["PayFlow-Event-Type"].ShouldBe("webhook.ping");
        var header = ping.Headers[WebhookSignature.HeaderName];
        header.Split(',').Count(p => p.StartsWith("v1=", StringComparison.Ordinal)).ShouldBe(2);
        WebhookSignature.Verify(ping.Body, header, newSecret, h.Clock.GetUtcNow()).ShouldBe(SignatureVerification.Valid);
        WebhookSignature.Verify(ping.Body, header, oldSecret, h.Clock.GetUtcNow()).ShouldBe(SignatureVerification.Valid);
    }

    [Fact]
    public async Task Disabled_or_unsubscribed_endpoints_receive_nothing()
    {
        await using var h = await HarnessAsync();
        var refundsOnly = StubWebhookReceiver.NewHost();
        var disabled = StubWebhookReceiver.NewHost();
        await CreateEndpointAsync(h, $"https://{refundsOnly}/hooks", "refund.succeeded", "refund.failed");
        var (disabledId, _) = await CreateEndpointAsync(h, $"https://{disabled}/hooks");
        await RefundScenario.PaidAsync(h.Merchant.Api, "30.00");
        await DispatchAsync(h); // delivery scheduled for the "*" endpoint

        (await h.Merchant.Owner.SendAsync(HttpMethod.Patch, $"/api/merchants/{h.Merchant.Id}/webhook-endpoints/{disabledId}", new { isEnabled = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        await SendAsync(h);

        h.Receiver.For(refundsOnly).ShouldBeEmpty();
        h.Receiver.For(disabled).ShouldBeEmpty();
        (await DeliveriesAsync(h)).GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString().ShouldBe("Cancelled");
    }

    [Fact]
    public async Task Merchants_only_see_their_own_events_and_deliveries()
    {
        await using var h = await HarnessAsync();
        var hostName = StubWebhookReceiver.NewHost();
        await CreateEndpointAsync(h, $"https://{hostName}/hooks");
        await RefundScenario.PaidAsync(h.Merchant.Api, "30.00");
        await DispatchAsync(h);
        var eventId = (await MerchantScenario.ReadAsync(await h.Merchant.Api.GetAsync(new Uri("/v1/events", UriKind.Relative))))
            .GetProperty("data").EnumerateArray().First().GetProperty("id").GetString();
        using var other = await PaymentScenario.PayingMerchantAsync(host, h.Factory);

        (await other.Api.GetAsync(new Uri($"/v1/events/{eventId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Owner.Http.GetAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-deliveries", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.Owner.Http.GetAsync(new Uri($"/api/merchants/{h.Merchant.Id}/webhook-endpoints", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task The_cookie_key_ring_is_encrypted_with_the_master_key()
    {
        await using var h = await HarnessAsync(); // logging in creates or uses a key

        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM identity.data_protection_keys")).ShouldBeGreaterThan(0);
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM identity.data_protection_keys WHERE xml NOT LIKE '%MasterKeyXmlDecryptor%' OR xml LIKE '%<masterKey%<value>%'")).ShouldBe(0);
    }
}
