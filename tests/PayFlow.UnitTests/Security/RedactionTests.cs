using PayFlow.Api.Logging;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Modules.Identity.Endpoints;
using PayFlow.SharedKernel.Security;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace PayFlow.UnitTests.Security;

public sealed class RedactionTests
{
    private sealed record Credentials(string User, [property: Sensitive] string Pin);

    [Theory]
    [InlineData("password", true)]
    [InlineData("X-Api-Key", true)]
    [InlineData("Authorization", true)]
    [InlineData("webhook_signing_secret", true)]
    [InlineData("pan", true)]
    [InlineData("company", false)]
    [InlineData("panel", false)]
    [InlineData("amount", false)]
    public void Recognises_sensitive_names(string name, bool expected) =>
        Redaction.IsSensitiveName(name).ShouldBe(expected);

    [Fact]
    public void Json_masks_attributed_and_sensitively_named_values_at_any_depth()
    {
        var json = RedactingJson.Serialize(new
        {
            email = "ops@example.test",
            password = "hunter2-hunter2",
            nested = new { apiKey = "pf_test_sk_abc", items = new[] { new { token = "t-1", note = "ok" } } },
            credentials = new Credentials("ops", "1234"),
        });

        json.ShouldContain("ops@example.test");
        json.ShouldContain("\"note\":\"ok\"");
        json.ShouldNotContain("hunter2");
        json.ShouldNotContain("pf_test_sk_abc");
        json.ShouldNotContain("t-1");
        json.ShouldNotContain("1234");
    }

    [Fact]
    public void Serilog_policy_never_logs_a_login_password()
    {
        var sink = new CollectingSink();
        using var logger = new LoggerConfiguration()
            .Destructure.With<SensitiveDataDestructuringPolicy>()
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("Login {@Request}", new LoginRequest("ops@example.test", "correct horse battery"));

        var rendered = sink.Events.Single().RenderMessage();
        rendered.ShouldContain("ops@example.test");
        rendered.ShouldNotContain("correct horse battery");
        rendered.ShouldContain(Redaction.Mask);
    }

    [Theory]
    [InlineData("0192f3c8a1b2c3d4", true)]
    [InlineData("req-2026-11-12:abc", true)]
    [InlineData("short", false)]
    [InlineData("has space in it", false)]
    [InlineData("line\nbreak-injection", false)]
    public void Correlation_ids_from_clients_are_validated(string value, bool expected) =>
        CorrelationContext.IsValid(value).ShouldBe(expected);

    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = [];

        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }
}
