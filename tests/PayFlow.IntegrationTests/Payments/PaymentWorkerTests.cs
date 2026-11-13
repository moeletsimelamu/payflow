using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Payments.Application;

namespace PayFlow.IntegrationTests.Payments;

/// <summary>Background jobs, driven explicitly with a fake clock (timers are off in tests).</summary>
public sealed class PaymentWorkerTests(PayFlowTestHost host)
{
    [Theory]
    [InlineData("sim_card_pending", 11, "Succeeded", "9.25")]
    [InlineData("sim_card_pending_then_declined", 11, "Declined", null)]
    [InlineData("sim_card_pending_expires", 31 * 60, "Expired", null)]
    public async Task Pending_payments_resolve_when_their_simulated_outcome_is_due(string method, int advanceSeconds, string finalStatus, string? fee)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(clock: clock);
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        var key = Guid.NewGuid().ToString("N");
        var body = PaymentScenario.Body("250.00", method: method);

        var created = await MerchantScenario.ReadAsync(await PaymentScenario.CreateAsync(m.Api, body, key));
        created.GetProperty("status").GetString().ShouldBe("Pending");
        var id = created.GetProperty("id").GetString()!;

        (await RunAsync<PendingPaymentsJob>(factory)).ShouldBe(0); // not due yet
        clock.Advance(TimeSpan.FromSeconds(advanceSeconds));
        (await RunAsync<PendingPaymentsJob>(factory)).ShouldBe(1);
        (await RunAsync<PendingPaymentsJob>(factory)).ShouldBe(0); // idempotent

        var resolved = await MerchantScenario.ReadAsync(await m.Api.GetAsync(new Uri($"/v1/payments/{id}", UriKind.Relative)));
        resolved.GetProperty("status").GetString().ShouldBe(finalStatus);
        resolved.GetProperty("statusHistory").EnumerateArray().Last().GetProperty("actorId").GetString().ShouldBe("pending-resolver");
        if (fee is null)
        {
            resolved.GetProperty("fee").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        }
        else
        {
            resolved.GetProperty("fee").GetString().ShouldBe(fee);
        }

        // An idempotent retry replays the original response (Pending); GET returns the current state.
        var replay = await MerchantScenario.ReadAsync(await PaymentScenario.CreateAsync(m.Api, body, key));
        replay.GetProperty("status").GetString().ShouldBe("Pending");
    }

    [Fact]
    public async Task A_request_that_crashed_after_its_first_transaction_is_completed_by_recovery()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(clock: clock);
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        var key = Guid.NewGuid().ToString("N");
        var request = new CreatePaymentRequest("100.00", "ZAR", "sim_card_success", "crash-order", null, null);

        // Run only steps 1–3 (claim + transaction 1), as if the process died before transaction 2.
        Guid paymentId;
        await using (var scope = factory.Services.CreateAsyncScope())
        {
            var service = scope.ServiceProvider.GetRequiredService<PaymentService>();
            var begun = await service.BeginAsync(PaymentScenario.DecodeMerchant(m.Id), key, request, CancellationToken.None);
            begun.IsSuccess.ShouldBeTrue();
            paymentId = begun.Value.Payment!.Id;
        }

        var body = new { amount = "100.00", currency = "ZAR", paymentMethod = "sim_card_success", merchantReference = "crash-order", description = (string?)null };
        var whileStuck = await PaymentScenario.CreateAsync(m.Api, body, key);
        whileStuck.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await MerchantScenario.ReadAsync(whileStuck)).GetProperty("code").GetString().ShouldBe("idempotency_request_in_progress");

        (await RunAsync<ProcessingRecoveryJob>(factory)).ShouldBe(0); // within the timeout: leave it alone
        clock.Advance(TimeSpan.FromSeconds(61));
        (await RunAsync<ProcessingRecoveryJob>(factory)).ShouldBeGreaterThanOrEqualTo(1);

        var retry = await PaymentScenario.CreateAsync(m.Api, body, key);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");
        var replayed = await MerchantScenario.ReadAsync(retry);
        replayed.GetProperty("status").GetString().ShouldBe("Succeeded");
        PaymentScenario.DecodePayment(replayed.GetProperty("id").GetString()!).ShouldBe(paymentId);

        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(95.10m);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM payments.payments WHERE merchant_id = @m",
            ("m", PaymentScenario.DecodeMerchant(m.Id)))).ShouldBe(1);
    }

    [Fact]
    public async Task A_slow_request_and_recovery_completing_the_same_payment_post_it_once()
    {
        // Regression: recovery used to race a request still finishing transaction 2, and the second
        // one crashed on the ledger's unique constraint. Now both lock the row; the second stands down.
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        await using var factory = host.CreateFactory(clock: clock);
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        for (var round = 0; round < 5; round++)
        {
            var request = new CreatePaymentRequest("100.00", "ZAR", "sim_card_success", $"race-{round}", null, null);
            Guid paymentId;
            await using (var scope = factory.Services.CreateAsyncScope())
            {
                var begun = await scope.ServiceProvider.GetRequiredService<PaymentService>()
                    .BeginAsync(PaymentScenario.DecodeMerchant(m.Id), Guid.NewGuid().ToString("N"), request, CancellationToken.None);
                paymentId = begun.Value.Payment!.Id;
            }

            clock.Advance(TimeSpan.FromSeconds(61));

            // The "original request" finishing transaction 2, at the same time as the recovery job.
            async Task FinishOriginalAsync()
            {
                await using var scope = factory.Services.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<PayFlow.Infrastructure.Persistence.PayFlowDbContext>();
                var payment = await db.Set<PayFlow.Modules.Payments.Domain.Payment>().SingleAsync(p => p.Id == paymentId);
                await scope.ServiceProvider.GetRequiredService<PaymentProcessor>().CompleteProcessingAsync(
                    payment, PayFlow.Modules.Payments.Domain.SimulatedDecision.Success, PayFlow.SharedKernel.Security.Actor.System("test"), CancellationToken.None);
            }

            await Task.WhenAll(FinishOriginalAsync(), RunAsync<ProcessingRecoveryJob>(factory));

            (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM ledger.journal_entries WHERE source_id = @p AND entry_type = 'PaymentCaptured'",
                ("p", paymentId))).ShouldBe(1, $"round {round}");
            (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM payments.payment_status_history WHERE payment_id = @p AND to_status = 'Succeeded'",
                ("p", paymentId))).ShouldBe(1, $"round {round}");
        }
    }

    private static async Task<int> RunAsync<TJob>(PayFlowApiFactory factory)
        where TJob : PayFlow.Infrastructure.Workers.IRecurringJob
    {
        await using var scope = factory.Services.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<TJob>().RunOnceAsync(CancellationToken.None);
    }
}
