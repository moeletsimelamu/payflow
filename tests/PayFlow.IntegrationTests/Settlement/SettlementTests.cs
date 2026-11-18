using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Refunds.Application;

namespace PayFlow.IntegrationTests.Settlement;

/// <summary>
/// Each test runs its own host on a fake clock so it can move past the settlement cut-off, and
/// every run is filtered to the test's own merchant: other tests share the database.
/// </summary>
public sealed class SettlementTests(PayFlowTestHost host)
{
    [Fact]
    public async Task The_worked_example_settles_to_ZAR_313_26_and_the_payout_empties_merchant_payable()
    {
        var (factory, clock) = Host();
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        var a = await RefundScenario.PaidAsync(m.Api, "100.00");
        await RefundScenario.PaidAsync(m.Api, "250.00");
        await RefundScenario.PaidAsync(m.Api, "19.99");
        await RefundOkAsync(factory, m.Api, a, "40.00");

        clock.Advance(TimeSpan.FromDays(2));
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var run = await RunAsync(admin, m.Id);

        run.GetProperty("skipped").GetArrayLength().ShouldBe(0);
        var batch = run.GetProperty("created").EnumerateArray().Single();
        batch.GetProperty("id").GetString()!.ShouldStartWith("stl_");
        batch.GetProperty("status").GetString().ShouldBe("Pending");
        batch.GetProperty("paymentCount").GetInt32().ShouldBe(3);
        batch.GetProperty("refundCount").GetInt32().ShouldBe(1);
        batch.GetProperty("grossPayments").GetString().ShouldBe("369.99");
        batch.GetProperty("totalFees").GetString().ShouldBe("16.73");
        batch.GetProperty("totalRefunds").GetString().ShouldBe("40.00");
        batch.GetProperty("netAmount").GetString().ShouldBe("313.26");
        batch.GetProperty("payoutAmount").GetString().ShouldBe("313.26");
        batch.GetProperty("carriedForwardOut").GetString().ShouldBe("0.00");
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(313.26m); // the ledger agrees before payout

        var batchId = batch.GetProperty("id").GetString()!;
        var paid = await admin.PostAsync($"/api/settlements/{batchId}/pay", new { simulateFailure = false });
        paid.StatusCode.ShouldBe(HttpStatusCode.OK, await paid.Content.ReadAsStringAsync());
        var paidBody = await DashboardClient.ReadJsonAsync(paid);
        paidBody.GetProperty("status").GetString().ShouldBe("Paid");
        paidBody.GetProperty("payoutReference").GetString()!.ShouldStartWith("SIM-PAYOUT-");
        paidBody.GetProperty("simulated").GetBoolean().ShouldBeTrue();

        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(0m);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM ledger.journal_entries WHERE entry_type = 'SettlementPaid' AND source_id = @b", ("b", Decode(batchId)))).ShouldBe(1);
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM platform.outbox_messages WHERE type = 'settlement.paid' AND payload::jsonb ->> 'settlementId' = @b", ("b", batchId))).ShouldBe(1);

        // The merchant sees its batch, with line items, and can export them (signed in after the clock moved).
        using var owner = await MerchantScenario.LoginAsync(host, m.Merchant.AdminEmail, m.Merchant.AdminPassword, factory);
        var detail = await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{m.Id}/settlements/{batchId}", UriKind.Relative)));
        detail.GetProperty("items").GetArrayLength().ShouldBe(4);
        var csv = await owner.Http.GetAsync(new Uri($"/api/merchants/{m.Id}/settlements/{batchId}/items.csv", UriKind.Relative));
        csv.StatusCode.ShouldBe(HttpStatusCode.OK);
        csv.Content.Headers.ContentType!.MediaType.ShouldBe("text/csv");
        var rows = (await csv.Content.ReadAsStringAsync()).TrimEnd('\n').Split('\n');
        rows[0].ShouldBe("settlement_id,type,source_id,payment_id,completed_at,gross,fee,net,currency");
        rows.Length.ShouldBe(5);
        rows.Skip(1).Select(r => decimal.Parse(r.Split(',')[7], System.Globalization.CultureInfo.InvariantCulture)).Sum().ShouldBe(313.26m);
    }

    [Fact]
    public async Task Parallel_runs_for_the_same_merchant_create_exactly_one_batch_and_settle_each_payment_once()
    {
        var (factory, clock) = Host();
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        for (var i = 0; i < 5; i++)
        {
            await RefundScenario.PaidAsync(m.Api, "10.00");
        }

        clock.Advance(TimeSpan.FromDays(2));
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var runs = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => RunAsync(admin, m.Id)));

        runs.Sum(r => r.GetProperty("created").GetArrayLength()).ShouldBe(1);
        runs.SelectMany(r => r.GetProperty("skipped").EnumerateArray()).Select(s => s.GetProperty("reason").GetString())
            .ShouldAllBe(reason => reason == "open_batch_exists" || reason == "concurrent_settlement" || reason == "nothing_to_settle");
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM settlement.batches WHERE merchant_id = @m", ("m", PaymentScenario.DecodeMerchant(m.Id)))).ShouldBe(1);
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM settlement.items i JOIN settlement.batches b ON b.id = i.batch_id WHERE b.merchant_id = @m", ("m", PaymentScenario.DecodeMerchant(m.Id)))).ShouldBe(5);

        // Once paid, a further run finds nothing new: the merchant is not even a target.
        var batchId = runs.SelectMany(r => r.GetProperty("created").EnumerateArray()).Single().GetProperty("id").GetString()!;
        (await admin.PostAsync($"/api/settlements/{batchId}/pay", new { simulateFailure = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var again = await RunAsync(admin, m.Id);
        again.GetProperty("created").GetArrayLength().ShouldBe(0);
        again.GetProperty("skipped").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public async Task A_refund_after_settlement_becomes_a_deficit_that_the_next_batch_nets()
    {
        var (factory, clock) = Host();
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        var payment = await RefundScenario.PaidAsync(m.Api, "100.00");
        clock.Advance(TimeSpan.FromDays(2));
        using (var admin = await MerchantScenario.AdminAsync(host, factory))
        {
            var first = Single(await RunAsync(admin, m.Id));
            first.GetProperty("payoutAmount").GetString().ShouldBe("95.10");
            (await admin.PostAsync($"/api/settlements/{first.GetProperty("id").GetString()}/pay", new { simulateFailure = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        // Refunding a settled payment is allowed; its full amount comes back off the merchant.
        await RefundOkAsync(factory, m.Api, payment, "100.00");
        clock.Advance(TimeSpan.FromDays(2));
        using (var admin = await MerchantScenario.AdminAsync(host, factory))
        {
            var deficit = Single(await RunAsync(admin, m.Id));
            deficit.GetProperty("netAmount").GetString().ShouldBe("-100.00");
            deficit.GetProperty("payoutAmount").GetString().ShouldBe("0.00");
            deficit.GetProperty("carriedForwardOut").GetString().ShouldBe("-100.00");
            deficit.GetProperty("status").GetString().ShouldBe("Paid"); // nothing to pay out
            deficit.GetProperty("payoutReference").GetString().ShouldBe("NO-PAYOUT");
        }

        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(-100.00m);
        await RefundScenario.PaidAsync(m.Api, "150.00"); // fee 6.35
        clock.Advance(TimeSpan.FromDays(2));
        using (var admin = await MerchantScenario.AdminAsync(host, factory))
        {
            var next = Single(await RunAsync(admin, m.Id));
            next.GetProperty("carriedForwardIn").GetString().ShouldBe("-100.00");
            next.GetProperty("netAmount").GetString().ShouldBe("43.65");
            next.GetProperty("payoutAmount").GetString().ShouldBe("43.65");
            next.GetProperty("carriedForwardOut").GetString().ShouldBe("0.00");
            (await admin.PostAsync($"/api/settlements/{next.GetProperty("id").GetString()}/pay", new { simulateFailure = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        }

        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(0m);
    }

    [Fact]
    public async Task A_failed_payout_posts_nothing_blocks_new_batches_and_can_be_retried_once()
    {
        var (factory, clock) = Host();
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        await RefundScenario.PaidAsync(m.Api, "100.00");
        clock.Advance(TimeSpan.FromDays(2));
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var batchId = Single(await RunAsync(admin, m.Id)).GetProperty("id").GetString()!;

        var failed = await DashboardClient.ReadJsonAsync(await admin.PostAsync($"/api/settlements/{batchId}/pay", new { simulateFailure = true }));
        failed.GetProperty("status").GetString().ShouldBe("Failed");
        failed.GetProperty("failureReason").GetString()!.ShouldContain("Simulated bank rejection");
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM ledger.journal_entries WHERE source_id = @b", ("b", Decode(batchId)))).ShouldBe(0);

        await RefundScenario.PaidAsync(m.Api, "20.00");
        clock.Advance(TimeSpan.FromDays(1));
        using var admin2 = await MerchantScenario.AdminAsync(host, factory);
        (await RunAsync(admin2, m.Id)).GetProperty("skipped").EnumerateArray().Single().GetProperty("reason").GetString().ShouldBe("open_batch_exists");

        (await admin2.PostAsync($"/api/settlements/{batchId}/pay", new { simulateFailure = false })).StatusCode.ShouldBe(HttpStatusCode.OK);
        var twice = await admin2.PostAsync($"/api/settlements/{batchId}/pay", new { simulateFailure = false });
        twice.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DashboardClient.ReadJsonAsync(twice)).GetProperty("code").GetString().ShouldBe("settlement_already_paid");
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM ledger.journal_entries WHERE entry_type = 'SettlementPaid' AND source_id = @b", ("b", Decode(batchId)))).ShouldBe(1);

        // The payment made while the batch was open goes into the next one.
        Single(await RunAsync(admin2, m.Id)).GetProperty("paymentCount").GetInt32().ShouldBe(1);
    }

    [Fact]
    public async Task A_settled_payment_cannot_be_reversed()
    {
        var (factory, clock) = Host();
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        var payment = await RefundScenario.PaidAsync(m.Api, "100.00");
        clock.Advance(TimeSpan.FromDays(2));
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        Single(await RunAsync(admin, m.Id));

        var reversal = await RefundScenario.ReverseAsync(m.Api, payment);

        reversal.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        var error = await MerchantScenario.ReadAsync(reversal);
        error.GetProperty("code").GetString().ShouldBe("payment_not_reversible");
        error.GetProperty("detail").GetString()!.ShouldContain("settled");
    }

    [Fact]
    public async Task Merchants_see_only_their_own_settlements_and_only_admins_run_or_pay()
    {
        var (factory, clock) = Host();
        await using var _ = factory;
        using var mine = await PaymentScenario.PayingMerchantAsync(host, factory);
        using var theirs = await PaymentScenario.PayingMerchantAsync(host, factory);
        await RefundScenario.PaidAsync(mine.Api, "100.00");
        clock.Advance(TimeSpan.FromDays(2));
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var batchId = Single(await RunAsync(admin, mine.Id)).GetProperty("id").GetString()!;
        using var owner = await MerchantScenario.LoginAsync(host, mine.Merchant.AdminEmail, mine.Merchant.AdminPassword, factory);
        using var other = await MerchantScenario.LoginAsync(host, theirs.Merchant.AdminEmail, theirs.Merchant.AdminPassword, factory);

        var list = await DashboardClient.ReadJsonAsync(await owner.Http.GetAsync(new Uri($"/api/merchants/{mine.Id}/settlements", UriKind.Relative)));
        list.GetProperty("items").EnumerateArray().Select(b => b.GetProperty("id").GetString()).ShouldBe([batchId]);

        (await other.Http.GetAsync(new Uri($"/api/merchants/{mine.Id}/settlements", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await other.Http.GetAsync(new Uri($"/api/merchants/{theirs.Id}/settlements/{batchId}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await other.Http.GetAsync(new Uri($"/api/merchants/{theirs.Id}/settlements/{batchId}/items.csv", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await owner.PostAsync("/api/settlements/runs", new { merchantId = mine.Id })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await owner.PostAsync($"/api/settlements/{batchId}/pay", new { simulateFailure = false })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task A_cutoff_in_the_future_is_rejected()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var response = await admin.PostAsync("/api/settlements/runs", new { cutoff = DateTimeOffset.UtcNow.AddDays(1) });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await DashboardClient.ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe("invalid_cutoff");
    }

    private (PayFlowApiFactory Factory, FakeTimeProvider Clock) Host()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        return (host.CreateFactory(clock: clock), clock);
    }

    private static async Task<JsonElement> RunAsync(DashboardClient admin, string merchantId)
    {
        var response = await admin.PostAsync("/api/settlements/runs", new { merchantId });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await DashboardClient.ReadJsonAsync(response);
    }

    private static JsonElement Single(JsonElement run)
    {
        run.GetProperty("skipped").GetArrayLength().ShouldBe(0, run.ToString());
        return run.GetProperty("created").EnumerateArray().Single();
    }

    /// <summary>Creates a refund and drives it to Succeeded (another test's job may get there first).</summary>
    private static async Task RefundOkAsync(PayFlowApiFactory factory, HttpClient api, string paymentId, string amount)
    {
        var created = await RefundScenario.RefundAsync(api, paymentId, amount);
        created.StatusCode.ShouldBe(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());
        var refundId = (await MerchantScenario.ReadAsync(created)).GetProperty("id").GetString()!;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await RefundScenario.RunJobAsync<RefundProcessingJob>(factory);
            var refund = await MerchantScenario.ReadAsync(await api.GetAsync(new Uri($"/v1/refunds/{refundId}", UriKind.Relative)));
            if (refund.GetProperty("status").GetString() == "Succeeded")
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"Refund {refundId} did not succeed.");
    }

    private static Guid Decode(string settlementId) =>
        PayFlow.SharedKernel.Ids.PublicId.TryParse(settlementId, "stl", out var id) ? id.Value : throw new ArgumentException(settlementId);
}
