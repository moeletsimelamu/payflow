using System.Diagnostics;
using PayFlow.Modules.Webhooks.Contracts;
using PayFlow.Modules.Webhooks.Infrastructure;

namespace PayFlow.UnitTests.Webhooks;

/// <summary>
/// Webhook requests go to merchants' servers, so they must not carry PayFlow's trace context
/// (traceparent, tracestate, baggage): trace ids are internal.
/// </summary>
public sealed class WebhookTracePropagationTests
{
    [Fact]
    public void The_webhook_handler_injects_no_trace_headers()
    {
        using var handler = SsrfGuard.CreateHandler(new WebhookOptions());
        using var activity = new Activity("deliver").SetIdFormat(ActivityIdFormat.W3C).Start();
        activity.AddBaggage("tenant", "secret-ish");

        Injected(handler.ActivityHeadersPropagator!, activity).ShouldBeEmpty();
    }

    [Fact]
    public void The_default_propagator_would_have_injected_them()
    {
        // Guards the test above: it would fail if the handler used the default propagator.
        using var activity = new Activity("deliver").SetIdFormat(ActivityIdFormat.W3C).Start();
        Injected(DistributedContextPropagator.CreateDefaultPropagator(), activity).ShouldContain("traceparent");
    }

    private static List<string> Injected(DistributedContextPropagator propagator, Activity activity)
    {
        var names = new List<string>();
        propagator.Inject(activity, carrier: null, (_, name, _) => names.Add(name));
        return names;
    }
}
