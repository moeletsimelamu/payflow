using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Security;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.Modules.Webhooks.Domain;
using PayFlow.Modules.Webhooks.Infrastructure;
using PayFlow.Webhooks.Signature;

namespace PayFlow.UnitTests.Webhooks;

public sealed class WebhookSignatureTests
{
    // Computed independently with Python: hmac.new(secret.encode(), f"{t}.{body}".encode(), hashlib.sha256).hexdigest()
    [Theory]
    [InlineData("whsec_test_secret_123", 1731405600L, "{\"id\":\"evt_1\",\"type\":\"payment.succeeded\"}", "c87b9fbc01cb589c867c7f34e13638fc8af978018c333066f38c906e709dc652")]
    [InlineData("another-secret", 1700000000L, "", "5e372a7fc105a6f861179aa6af75bd388bec35e609da1aab99a0566c5ac08c9b")]
    public void Signatures_match_independently_computed_vectors(string secret, long t, string body, string expected) =>
        WebhookSignature.ComputeV1(secret, t, body).ShouldBe(expected);

    private const string Secret = "whsec_test_secret_123";
    private const string Body = "{\"id\":\"evt_1\",\"type\":\"payment.succeeded\"}";
    private static readonly DateTimeOffset SignedAt = DateTimeOffset.FromUnixTimeSeconds(1731405600);

    [Fact]
    public void A_genuine_delivery_verifies()
    {
        var header = WebhookSignature.CreateHeader(1731405600, Body, [Secret]);

        header.ShouldBe("t=1731405600,v1=c87b9fbc01cb589c867c7f34e13638fc8af978018c333066f38c906e709dc652");
        WebhookSignature.Verify(Body, header, Secret, SignedAt.AddSeconds(3)).ShouldBe(SignatureVerification.Valid);
    }

    [Fact]
    public void Tampering_with_body_timestamp_or_secret_is_detected()
    {
        var header = WebhookSignature.CreateHeader(1731405600, Body, [Secret]);

        WebhookSignature.Verify(Body.Replace("succeeded", "failed", StringComparison.Ordinal), header, Secret, SignedAt).ShouldBe(SignatureVerification.SignatureMismatch);
        WebhookSignature.Verify(Body, header.Replace("t=1731405600", "t=1731405601", StringComparison.Ordinal), Secret, SignedAt).ShouldBe(SignatureVerification.SignatureMismatch);
        WebhookSignature.Verify(Body, header, "whsec_other", SignedAt).ShouldBe(SignatureVerification.SignatureMismatch);
    }

    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    [InlineData(-86400)]
    public void Replays_outside_the_tolerance_window_are_rejected(int skewSeconds)
    {
        var header = WebhookSignature.CreateHeader(1731405600, Body, [Secret]);

        WebhookSignature.Verify(Body, header, Secret, SignedAt.AddSeconds(-skewSeconds)).ShouldBe(SignatureVerification.TimestampOutsideTolerance);
    }

    [Fact]
    public void During_rotation_either_secret_verifies()
    {
        var header = WebhookSignature.CreateHeader(1731405600, Body, ["whsec_new", "whsec_old"]);

        header.Split(',').Count(p => p.StartsWith("v1=", StringComparison.Ordinal)).ShouldBe(2);
        WebhookSignature.Verify(Body, header, "whsec_new", SignedAt).ShouldBe(SignatureVerification.Valid);
        WebhookSignature.Verify(Body, header, "whsec_old", SignedAt).ShouldBe(SignatureVerification.Valid);
        WebhookSignature.Verify(Body, header, "whsec_neither", SignedAt).ShouldBe(SignatureVerification.SignatureMismatch);
    }

    [Theory]
    [InlineData(null, SignatureVerification.MissingHeader)]
    [InlineData("", SignatureVerification.MissingHeader)]
    [InlineData("v1=abc", SignatureVerification.MalformedHeader)]
    [InlineData("t=1731405600", SignatureVerification.MalformedHeader)]
    [InlineData("t=abc,v1=c87b9fbc01cb589c867c7f34e13638fc8af978018c333066f38c906e709dc652", SignatureVerification.MalformedHeader)]
    [InlineData("garbage", SignatureVerification.MalformedHeader)]
    public void Malformed_headers_are_rejected(string? header, SignatureVerification expected) =>
        WebhookSignature.Verify(Body, header, Secret, SignedAt).ShouldBe(expected);
}

public sealed class WebhookRetryTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public void The_schedule_is_eight_attempts_over_about_28_hours()
    {
        Enumerable.Range(1, 7).Select(n => RetrySchedule.DelayAfter(n, 0)!.Value).ShouldBe(
        [
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30), TimeSpan.FromHours(2),
            TimeSpan.FromHours(5), TimeSpan.FromHours(10), TimeSpan.FromHours(10),
        ]);
        RetrySchedule.DelayAfter(8, 0).ShouldBeNull();
        RetrySchedule.Delays.Aggregate(TimeSpan.Zero, (a, b) => a + b).ShouldBe(TimeSpan.FromMinutes(1 + 5 + 30 + 120 + 300 + 600 + 600));
    }

    [Theory]
    [InlineData(-20, 48)]
    [InlineData(20, 72)]
    public void Jitter_stays_within_twenty_percent(int jitter, int expectedSeconds) =>
        RetrySchedule.DelayAfter(1, jitter).ShouldBe(TimeSpan.FromSeconds(expectedSeconds));

    [Fact]
    public void Jitter_outside_the_band_is_a_programming_error() =>
        Should.Throw<ArgumentOutOfRangeException>(() => RetrySchedule.DelayAfter(1, 21));

    [Fact]
    public void A_delivery_retries_until_exhausted_and_a_redelivery_gets_a_fresh_budget()
    {
        var delivery = WebhookDelivery.Schedule(Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Guid.CreateVersion7(), Now);

        delivery.RecordFailure(Now, 0);
        delivery.Status.ShouldBe(DeliveryStatus.Retrying);
        delivery.NextAttemptAt.ShouldBe(Now.AddMinutes(1));
        for (var i = 2; i <= 8; i++)
        {
            delivery.RecordFailure(Now, 0);
        }

        delivery.Status.ShouldBe(DeliveryStatus.Exhausted);
        delivery.AttemptCount.ShouldBe(8);

        delivery.Redeliver(Now).IsSuccess.ShouldBeTrue();
        delivery.Status.ShouldBe(DeliveryStatus.Pending);
        delivery.NextAttemptNumber.ShouldBe(9);
        delivery.RecordFailure(Now, 0);
        delivery.Status.ShouldBe(DeliveryStatus.Retrying); // budget counts from the redelivery
        delivery.Redeliver(Now).Error!.Code.ShouldBe("delivery_in_progress");
    }

    [Fact]
    public void The_previous_secret_signs_only_during_the_overlap()
    {
        var endpoint = WebhookEndpoint.Create(Guid.CreateVersion7(), Guid.CreateVersion7(), "https://shop.example/hooks", null, ["*"], "old", "abcd", Now);
        endpoint.RollSecret("new", "wxyz", TimeSpan.FromHours(24), Now);

        endpoint.SigningSecretCiphertexts(Now.AddHours(23)).ShouldBe(["new", "old"]);
        endpoint.SigningSecretCiphertexts(Now.AddHours(24)).ShouldBe(["new"]);
        endpoint.Subscribes("refund.failed").ShouldBeTrue();
    }
}

public sealed class SsrfGuardTests
{
    private static readonly WebhookOptions Strict = new();

    [Theory]
    [InlineData("8.8.8.8", true)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.1.2.3", false)]
    [InlineData("172.16.0.1", false)]
    [InlineData("172.31.250.5", false)]
    [InlineData("192.168.1.1", false)]
    [InlineData("169.254.169.254", false)] // cloud metadata
    [InlineData("100.64.0.1", false)]
    [InlineData("0.0.0.0", false)]
    [InlineData("224.0.0.1", false)]
    [InlineData("255.255.255.255", false)]
    [InlineData("::1", false)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("::ffff:127.0.0.1", false)] // IPv4-mapped loopback
    [InlineData("::ffff:10.0.0.1", false)]
    [InlineData("64:ff9b::a00:1", false)] // NAT64 of 10.0.0.1
    public void Only_public_addresses_are_allowed(string address, bool expected) =>
        SsrfGuard.IsPublic(IPAddress.Parse(address)).ShouldBe(expected);

    [Theory]
    [InlineData("https://shop.example.com/hooks", true)]
    [InlineData("https://shop.example.com:8443/hooks?x=1", true)]
    [InlineData("http://shop.example.com/hooks", false)]
    [InlineData("https://user:pass@shop.example.com/hooks", false)]
    [InlineData("https://localhost/hooks", false)]
    [InlineData("https://api.localhost/hooks", false)]
    [InlineData("https://payflow.internal/hooks", false)]
    [InlineData("https://webhook-sink/hooks", false)] // single-label name: internal
    [InlineData("https://127.0.0.1/hooks", false)]
    [InlineData("https://[::1]/hooks", false)]
    [InlineData("https://169.254.169.254/latest/meta-data", false)]
    [InlineData("ftp://shop.example.com/hooks", false)]
    [InlineData("not a url", false)]
    public void Endpoint_urls_are_checked_when_saved(string url, bool allowed) =>
        SsrfGuard.ValidateUrl(url, Strict).IsSuccess.ShouldBe(allowed);

    [Fact]
    public void Development_options_allow_the_compose_sink() =>
        SsrfGuard.ValidateUrl("http://webhook-sink:8080/webhooks", new WebhookOptions { AllowHttp = true, AllowPrivateNetworks = true }).IsSuccess.ShouldBeTrue();

    [Theory]
    [InlineData("http://localhost:1/")]
    [InlineData("http://127.0.0.1:1/")]
    public async Task The_handler_refuses_to_connect_to_private_addresses_at_connect_time(string url)
    {
        // Connect-time check: this is what defeats DNS rebinding, because the address that is
        // actually dialled is the one checked.
        using var client = new HttpClient(SsrfGuard.CreateHandler(Strict));

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri(url)));

        FindInner<SsrfBlockedException>(error).ShouldNotBeNull();
    }

    [Fact]
    public async Task With_private_networks_allowed_the_connection_is_attempted()
    {
        using var client = new HttpClient(SsrfGuard.CreateHandler(new WebhookOptions { AllowPrivateNetworks = true }));

        var error = await Should.ThrowAsync<HttpRequestException>(() => client.GetAsync(new Uri("http://127.0.0.1:1/")));

        FindInner<SsrfBlockedException>(error).ShouldBeNull(); // refused by the OS, not by the guard
    }

    private static T? FindInner<T>(Exception? e)
        where T : Exception
    {
        for (; e is not null; e = e.InnerException)
        {
            if (e is T match)
            {
                return match;
            }
        }

        return null;
    }
}

public sealed class SecretProtectorTests
{
    private static SecretProtector Protector(string? key, string environment = "Testing") => new(
        Options.Create(new MasterKeyOptions { MasterKey = key }),
        new FakeEnvironment(environment),
        NullLogger<SecretProtector>.Instance);

    private static readonly string KeyA = Convert.ToBase64String(Enumerable.Repeat((byte)1, 32).ToArray());
    private static readonly string KeyB = Convert.ToBase64String(Enumerable.Repeat((byte)2, 32).ToArray());

    [Fact]
    public void Round_trips_and_uses_a_fresh_nonce_each_time()
    {
        var protector = Protector(KeyA);

        var first = protector.Protect("whsec_abc", "webhooks");
        var second = protector.Protect("whsec_abc", "webhooks");

        first.ShouldStartWith("v1.");
        first.ShouldNotBe(second);
        first.ShouldNotContain("whsec_abc");
        protector.Unprotect(first, "webhooks").ShouldBe("whsec_abc");
    }

    [Fact]
    public void Wrong_purpose_wrong_key_or_tampering_fails()
    {
        var protector = Protector(KeyA);
        var value = protector.Protect("whsec_abc", "webhooks");
        var bytes = Convert.FromBase64String(value[3..]);
        bytes[^1] ^= 0x01;
        var tampered = "v1." + Convert.ToBase64String(bytes);

        Should.Throw<CryptographicException>(() => protector.Unprotect(value, "other-purpose"));
        Should.Throw<CryptographicException>(() => Protector(KeyB).Unprotect(value, "webhooks"));
        Should.Throw<CryptographicException>(() => protector.Unprotect(tampered, "webhooks"));
    }

    [Fact]
    public void A_missing_or_short_master_key_fails_fast()
    {
        Should.Throw<InvalidOperationException>(() => Protector(null, Environments.Production));
        Should.Throw<InvalidOperationException>(() => Protector(Convert.ToBase64String(new byte[16])));
    }

    private sealed class FakeEnvironment(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;

        public string ApplicationName { get; set; } = "PayFlow.Tests";

        public string ContentRootPath { get; set; } = AppContext.BaseDirectory;

        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
