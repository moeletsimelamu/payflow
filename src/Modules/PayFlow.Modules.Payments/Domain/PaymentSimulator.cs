namespace PayFlow.Modules.Payments.Domain;

internal enum SimulatedOutcome
{
    Succeeded,
    Declined,
    Failed,
    Pending,
    Expired,
}

internal sealed record SimulatedDecision(SimulatedOutcome Outcome, string? FailureCode = null, string? FailureReason = null)
{
    public static SimulatedDecision Success { get; } = new(SimulatedOutcome.Succeeded);
}

/// <summary>
/// Deterministic stand-in for a card network. The outcome depends only on the simulation token, so
/// a crashed request can be completed later with exactly the same result (docs/PAYMENT_LIFECYCLE.md §3).
/// These tokens are obviously fake: no real card data is ever accepted.
/// </summary>
internal static class PaymentSimulator
{
    /// <summary>How long a "pending" simulation waits before resolving.</summary>
    public static readonly TimeSpan PendingResolution = TimeSpan.FromSeconds(10);

    /// <summary>A pending payment that never resolves expires after this.</summary>
    public static readonly TimeSpan PendingExpiry = TimeSpan.FromMinutes(30);

    private static readonly Dictionary<string, (SimulatedDecision Initial, SimulatedDecision? AfterPending)> Methods = new(StringComparer.Ordinal)
    {
        ["sim_card_success"] = (SimulatedDecision.Success, null),
        ["sim_card_declined_insufficient_funds"] = (new(SimulatedOutcome.Declined, "insufficient_funds", "The simulated card has insufficient funds."), null),
        ["sim_card_declined_do_not_honour"] = (new(SimulatedOutcome.Declined, "do_not_honour", "The simulated issuer declined the payment."), null),
        ["sim_card_processor_error"] = (new(SimulatedOutcome.Failed, "processor_error", "The simulated processor returned an error."), null),
        ["sim_card_pending"] = (new(SimulatedOutcome.Pending), SimulatedDecision.Success),
        ["sim_card_pending_then_declined"] = (new(SimulatedOutcome.Pending), new(SimulatedOutcome.Declined, "do_not_honour", "The simulated issuer declined the payment after review.")),
        ["sim_card_pending_expires"] = (new(SimulatedOutcome.Pending), new(SimulatedOutcome.Expired, "payment_expired", "The payment was not confirmed in time.")),
        ["sim_card_refund_fails"] = (SimulatedDecision.Success, null),
    };

    public static IReadOnlyCollection<string> SupportedMethods => Methods.Keys;

    public static bool IsSupported(string? method) => method is not null && Methods.ContainsKey(method);

    public static SimulatedDecision Decide(string method) => Methods[method].Initial;

    /// <summary>The final outcome of a pending payment.</summary>
    public static SimulatedDecision ResolvePending(string method) =>
        Methods[method].AfterPending ?? throw new InvalidOperationException($"{method} never goes pending.");

    /// <summary>When the pending payment should be resolved.</summary>
    public static TimeSpan PendingDelay(string method) =>
        ResolvePending(method).Outcome == SimulatedOutcome.Expired ? PendingExpiry : PendingResolution;
}
