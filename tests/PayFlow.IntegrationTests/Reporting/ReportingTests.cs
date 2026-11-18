using System.Net;
using System.Text.Json;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Refunds.Application;

namespace PayFlow.IntegrationTests.Reporting;

/// <summary>Reports are checked against a merchant whose every payment the test made, so each figure is known.</summary>
public sealed class ReportingTests(PayFlowTestHost host)
{
    [Fact]
    public async Task A_merchant_report_counts_volume_outcomes_refunds_and_failure_codes_exactly()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var a = await RefundScenario.PaidAsync(m.Api, "100.00");
        await RefundScenario.PaidAsync(m.Api, "250.00");
        await RefundScenario.PaidAsync(m.Api, "19.99");
        var reversed = await RefundScenario.PaidAsync(m.Api, "42.00");
        (await RefundScenario.ReverseAsync(m.Api, reversed)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await RefundScenario.PaidAsync(m.Api, "10.00", method: "sim_card_declined_insufficient_funds");
        await RefundScenario.PaidAsync(m.Api, "11.00", method: "sim_card_declined_insufficient_funds");
        await RefundScenario.PaidAsync(m.Api, "5.00", method: "sim_card_processor_error");
        await RefundSucceededAsync(m.Api, a, "40.00");

        var report = await ReportAsync(m.Owner, $"/api/merchants/{m.Id}/reports?currency=ZAR");

        report.GetProperty("merchantId").GetString().ShouldBe(m.Id);
        var volume = report.GetProperty("volume");
        volume.GetProperty("payments").GetInt32().ShouldBe(7);
        volume.GetProperty("succeeded").GetInt32().ShouldBe(4); // captured or later reversed
        volume.GetProperty("succeededAmount").GetString().ShouldBe("411.99");
        volume.GetProperty("days").GetArrayLength().ShouldBe(30);
        volume.GetProperty("days").EnumerateArray().Sum(d => d.GetProperty("payments").GetInt32()).ShouldBe(7);

        var outcomes = report.GetProperty("outcomes");
        outcomes.GetProperty("succeeded").GetInt32().ShouldBe(3);
        outcomes.GetProperty("reversed").GetInt32().ShouldBe(1);
        outcomes.GetProperty("declined").GetInt32().ShouldBe(2);
        outcomes.GetProperty("failed").GetInt32().ShouldBe(1);
        outcomes.GetProperty("successRate").GetDecimal().ShouldBe(0.5714m);
        outcomes.GetProperty("declineRate").GetDecimal().ShouldBe(0.2857m);
        outcomes.GetProperty("failureRate").GetDecimal().ShouldBe(0.1429m);
        outcomes.GetProperty("topFailureCodes").EnumerateArray()
            .Select(c => $"{c.GetProperty("code").GetString()}:{c.GetProperty("count").GetInt32()}")
            .ShouldBe(["insufficient_funds:2", "processor_error:1"]);

        var refunds = report.GetProperty("refunds");
        refunds.GetProperty("count").GetInt32().ShouldBe(1);
        refunds.GetProperty("amount").GetString().ShouldBe("40.00");
        refunds.GetProperty("refundRate").GetDecimal().ShouldBe(0.0971m);

        report.GetProperty("settlements").GetProperty("batches").GetInt32().ShouldBe(0);
        report.GetProperty("reconciliation").ValueKind.ShouldBe(JsonValueKind.Null); // platform-only
        var performance = report.GetProperty("performance");
        performance.GetProperty("processedPayments").GetInt32().ShouldBeGreaterThanOrEqualTo(4);
        performance.GetProperty("processingP50Ms").ValueKind.ShouldBe(JsonValueKind.Number);

        // Other currencies are reported separately, never summed.
        var usd = await ReportAsync(m.Owner, $"/api/merchants/{m.Id}/reports?currency=USD");
        usd.GetProperty("volume").GetProperty("payments").GetInt32().ShouldBe(0);
        usd.GetProperty("outcomes").GetProperty("successRate").GetDecimal().ShouldBe(0m);
    }

    [Fact]
    public async Task Reports_are_cached_for_a_minute_per_query()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        await RefundScenario.PaidAsync(m.Api, "10.00");
        var first = await ReportAsync(m.Owner, $"/api/merchants/{m.Id}/reports");

        await RefundScenario.PaidAsync(m.Api, "20.00");
        var cached = await ReportAsync(m.Owner, $"/api/merchants/{m.Id}/reports");
        var otherWindow = await ReportAsync(m.Owner, $"/api/merchants/{m.Id}/reports?from={DateTime.UtcNow.AddDays(-6):yyyy-MM-dd}");

        cached.GetProperty("generatedAt").GetString().ShouldBe(first.GetProperty("generatedAt").GetString());
        cached.GetProperty("volume").GetProperty("payments").GetInt32().ShouldBe(1);
        otherWindow.GetProperty("volume").GetProperty("payments").GetInt32().ShouldBe(2); // a different key is computed fresh
        otherWindow.GetProperty("volume").GetProperty("days").GetArrayLength().ShouldBe(7);
    }

    [Fact]
    public async Task Platform_admins_see_any_merchant_and_the_reconciliation_backlog()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        await RefundScenario.PaidAsync(m.Api, "10.00");
        using var admin = await MerchantScenario.AdminAsync(host);

        var one = await ReportAsync(admin, $"/api/reports?merchantId={m.Id}");
        one.GetProperty("volume").GetProperty("payments").GetInt32().ShouldBe(1);
        var reconciliation = one.GetProperty("reconciliation");
        reconciliation.GetProperty("byAge").GetArrayLength().ShouldBe(3);
        reconciliation.GetProperty("unresolved").GetInt32()
            .ShouldBe(reconciliation.GetProperty("byAge").EnumerateArray().Sum(b => b.GetProperty("open").GetInt32()));

        var all = await ReportAsync(admin, "/api/reports");
        all.GetProperty("merchantId").ValueKind.ShouldBe(JsonValueKind.Null);
        all.GetProperty("volume").GetProperty("payments").GetInt32().ShouldBeGreaterThanOrEqualTo(1);
    }

    [Fact]
    public async Task Merchants_cannot_read_other_merchants_or_platform_reports_and_bad_queries_are_400()
    {
        using var mine = await PaymentScenario.PayingMerchantAsync(host);
        using var theirs = await PaymentScenario.PayingMerchantAsync(host);

        (await theirs.Owner.Http.GetAsync(new Uri($"/api/merchants/{mine.Id}/reports", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await mine.Owner.Http.GetAsync(new Uri("/api/reports", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await mine.Api.GetAsync(new Uri($"/api/merchants/{mine.Id}/reports", UriKind.Relative))).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);

        foreach (var (query, code) in new[]
                 {
                     ("currency=XYZ", "unsupported_currency"),
                     ("from=2026-11-10&to=2026-11-01", "invalid_period"),
                     ("from=2020-01-01&to=2026-11-01", "invalid_period"),
                 })
        {
            var response = await mine.Owner.Http.GetAsync(new Uri($"/api/merchants/{mine.Id}/reports?{query}", UriKind.Relative));
            response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
            (await DashboardClient.ReadJsonAsync(response)).GetProperty("code").GetString().ShouldBe(code);
        }
    }

    private static async Task<JsonElement> ReportAsync(DashboardClient client, string url)
    {
        var response = await client.Http.GetAsync(new Uri(url, UriKind.Relative));
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await DashboardClient.ReadJsonAsync(response);
    }

    private async Task RefundSucceededAsync(HttpClient api, string paymentId, string amount)
    {
        var created = await RefundScenario.RefundAsync(api, paymentId, amount);
        created.StatusCode.ShouldBe(HttpStatusCode.Created);
        var refundId = (await MerchantScenario.ReadAsync(created)).GetProperty("id").GetString()!;
        for (var attempt = 0; attempt < 20; attempt++)
        {
            await RefundScenario.RunJobAsync<RefundProcessingJob>(host.Factory);
            if ((await MerchantScenario.ReadAsync(await api.GetAsync(new Uri($"/v1/refunds/{refundId}", UriKind.Relative)))).GetProperty("status").GetString() == "Succeeded")
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException($"Refund {refundId} did not succeed.");
    }
}
