using System.Net;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;
using PayFlow.Modules.Refunds.Application;

namespace PayFlow.IntegrationTests.Refunds;

/// <summary>
/// The refund processor is shared by all tests (one database), so tests never assert that a refund
/// is still Pending after the request returns: another test may have run the job in between.
/// </summary>
public sealed class RefundTests(PayFlowTestHost host)
{
    [Fact]
    public async Task Partial_then_final_refund_moves_the_payment_through_its_lifecycle_and_the_ledger()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00");

        var partial = await RefundScenario.RefundAsync(m.Api, paymentId, "40.00", reason: "Item returned");
        partial.StatusCode.ShouldBe(HttpStatusCode.Created);
        var refund = await MerchantScenario.ReadAsync(partial);
        refund.GetProperty("id").GetString()!.ShouldStartWith("re_");
        refund.GetProperty("status").GetString().ShouldBe("Pending");
        refund.GetProperty("amount").GetString().ShouldBe("40.00");
        (await RefundScenario.GetPaymentAsync(m.Api, paymentId)).GetProperty("refundableAmount").GetString().ShouldBe("60.00");

        await RefundScenario.RunJobAsync<RefundProcessingJob>(host.Factory);
        var afterPartial = await RefundScenario.GetPaymentAsync(m.Api, paymentId);
        afterPartial.GetProperty("status").GetString().ShouldBe("PartiallyRefunded");
        afterPartial.GetProperty("refundedAmount").GetString().ShouldBe("40.00");
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(55.10m); // 95.10 − 40.00

        // No amount: refund whatever is left.
        var rest = await MerchantScenario.ReadAsync(await RefundScenario.RefundAsync(m.Api, paymentId, null));
        rest.GetProperty("amount").GetString().ShouldBe("60.00");
        await RefundScenario.RunJobAsync<RefundProcessingJob>(host.Factory);

        var final = await RefundScenario.GetPaymentAsync(m.Api, paymentId);
        final.GetProperty("status").GetString().ShouldBe("Refunded");
        final.GetProperty("refundableAmount").GetString().ShouldBe("0.00");
        final.GetProperty("statusHistory").EnumerateArray().Select(h => h.GetProperty("to").GetString())
            .ShouldBe(["Created", "Processing", "Succeeded", "PartiallyRefunded", "Refunded"]);
        // Refunds keep the fee (decision 2): the merchant is down exactly the 4.90 fee.
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(-4.90m);

        var history = await MerchantScenario.ReadAsync(await m.Api.GetAsync(new Uri($"/v1/payments/{paymentId}/refunds", UriKind.Relative)));
        history.GetProperty("refunds").EnumerateArray().Select(r => r.GetProperty("status").GetString()).ShouldBe(["Succeeded", "Succeeded"]);
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM platform.outbox_messages WHERE type = 'refund.succeeded' AND payload->>'paymentId' = @p", ("p", paymentId))).ShouldBe(2);
    }

    [Fact]
    public async Task Twenty_concurrent_refunds_can_never_oversubscribe_a_payment()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => RefundScenario.RefundAsync(m.Api, paymentId, "10.00")));

        responses.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(10);
        var rejected = responses.Where(r => r.StatusCode != HttpStatusCode.Created).ToList();
        rejected.Count.ShouldBe(10);
        foreach (var r in rejected)
        {
            r.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
            (await MerchantScenario.ReadAsync(r)).GetProperty("code").GetString().ShouldBe("refund_exceeds_refundable_balance");
        }

        var paymentGuid = PaymentScenario.DecodePayment(paymentId);
        (await PaymentScenario.DecimalAsync(host, "SELECT refunded_amount + pending_refund_amount FROM payments.payments WHERE id = @p", ("p", paymentGuid))).ShouldBe(100.00m);

        await RefundScenario.RunJobAsync<RefundProcessingJob>(host.Factory);
        var final = await RefundScenario.GetPaymentAsync(m.Api, paymentId);
        final.GetProperty("status").GetString().ShouldBe("Refunded");
        final.GetProperty("refundedAmount").GetString().ShouldBe("100.00");
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM ledger.journal_entries e JOIN refunds.refunds r ON r.id = e.source_id WHERE r.payment_id = @p AND e.entry_type = 'RefundSucceeded'",
            ("p", paymentGuid))).ShouldBe(10);
    }

    [Fact]
    public async Task A_failed_refund_releases_its_reservation_and_moves_no_money()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00", method: "sim_card_refund_fails");
        var refundId = (await MerchantScenario.ReadAsync(await RefundScenario.RefundAsync(m.Api, paymentId, "100.00"))).GetProperty("id").GetString();

        await RefundScenario.RunJobAsync<RefundProcessingJob>(host.Factory);

        var refund = await MerchantScenario.ReadAsync(await m.Api.GetAsync(new Uri($"/v1/refunds/{refundId}", UriKind.Relative)));
        refund.GetProperty("status").GetString().ShouldBe("Failed");
        refund.GetProperty("failureReason").GetString().ShouldNotBeNullOrEmpty();
        var payment = await RefundScenario.GetPaymentAsync(m.Api, paymentId);
        payment.GetProperty("status").GetString().ShouldBe("Succeeded");
        payment.GetProperty("refundableAmount").GetString().ShouldBe("100.00");
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(95.10m);
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM platform.outbox_messages WHERE type = 'refund.failed' AND payload->>'refundId' = @r", ("r", refundId!))).ShouldBe(1);
    }

    [Theory]
    [InlineData("100.01", 422, "refund_exceeds_refundable_balance")]
    [InlineData("10.001", 400, "invalid_amount")]
    [InlineData("-5.00", 400, "invalid_amount")]
    public async Task Invalid_refund_amounts_are_rejected(string amount, int status, string code)
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00");

        var response = await RefundScenario.RefundAsync(m.Api, paymentId, amount);

        ((int)response.StatusCode).ShouldBe(status);
        (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Fact]
    public async Task Declined_payments_cannot_be_refunded()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00", method: "sim_card_declined_do_not_honour");

        var response = await RefundScenario.RefundAsync(m.Api, paymentId, "10.00");

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe("payment_not_refundable");
    }

    [Fact]
    public async Task Refund_requests_are_idempotent_even_under_concurrency()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00");
        var key = Guid.NewGuid().ToString("N");

        var responses = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => RefundScenario.RefundAsync(m.Api, paymentId, "25.00", key)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM refunds.refunds WHERE payment_id = @p", ("p", PaymentScenario.DecodePayment(paymentId)))).ShouldBe(1);
        (await RefundScenario.GetPaymentAsync(m.Api, paymentId)).GetProperty("refundableAmount").GetString().ShouldBe("75.00");

        var changed = await RefundScenario.RefundAsync(m.Api, paymentId, "30.00", key);
        changed.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MerchantScenario.ReadAsync(changed)).GetProperty("code").GetString().ShouldBe("idempotency_key_reused");
    }

    [Fact]
    public async Task A_reversal_cancels_the_payment_and_returns_the_fee()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "250.00");
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(240.75m);
        var key = Guid.NewGuid().ToString("N");

        var reversed = await RefundScenario.ReverseAsync(m.Api, paymentId, key);

        reversed.StatusCode.ShouldBe(HttpStatusCode.Created);
        var body = await MerchantScenario.ReadAsync(reversed);
        body.GetProperty("id").GetString()!.ShouldStartWith("rv_");
        body.GetProperty("amount").GetString().ShouldBe("250.00");
        body.GetProperty("feeReturned").GetString().ShouldBe("9.25");
        (await RefundScenario.GetPaymentAsync(m.Api, paymentId)).GetProperty("status").GetString().ShouldBe("Reversed");
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(0m);

        var replay = await RefundScenario.ReverseAsync(m.Api, paymentId, key);
        replay.StatusCode.ShouldBe(HttpStatusCode.Created);
        replay.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");

        var again = await RefundScenario.ReverseAsync(m.Api, paymentId);
        again.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MerchantScenario.ReadAsync(again)).GetProperty("code").GetString().ShouldBe("payment_not_reversible");
        (await MerchantScenario.ReadAsync(await RefundScenario.RefundAsync(m.Api, paymentId, "1.00"))).GetProperty("code").GetString().ShouldBe("payment_not_refundable");
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM platform.outbox_messages WHERE type = 'payment.reversed' AND payload->>'paymentId' = @p", ("p", paymentId))).ShouldBe(1);
    }

    [Fact]
    public async Task A_refunded_or_refunding_payment_cannot_be_reversed()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00");
        (await RefundScenario.RefundAsync(m.Api, paymentId, "10.00")).StatusCode.ShouldBe(HttpStatusCode.Created);

        var response = await RefundScenario.ReverseAsync(m.Api, paymentId);

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe("payment_not_reversible");
    }

    [Fact]
    public async Task A_racing_refund_and_reversal_never_both_succeed()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        for (var round = 0; round < 5; round++)
        {
            var paymentId = await RefundScenario.PaidAsync(m.Api, "100.00");

            var results = await Task.WhenAll(RefundScenario.RefundAsync(m.Api, paymentId, "100.00"), RefundScenario.ReverseAsync(m.Api, paymentId));

            results.Count(r => r.StatusCode == HttpStatusCode.Created).ShouldBe(1, $"round {round}");
            results.Count(r => r.StatusCode == HttpStatusCode.UnprocessableEntity).ShouldBe(1, $"round {round}");
        }
    }

    [Fact]
    public async Task Operators_refund_within_their_limit_and_platform_admins_cannot_move_merchant_money()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        using var operatorClient = await RefundScenario.MemberAsync(host, m.Owner, m.Id, "MerchantOperator");
        var zarPayment = await RefundScenario.PaidAsync(m.Api, "2000.00");
        var usdPayment = await RefundScenario.PaidAsync(m.Api, "50.00", currency: "USD");
        string Url(string payment, string action = "refunds") => $"/api/merchants/{m.Id}/payments/{payment}/{action}";

        // ZAR operator limit is 1000.00 (MerchantScenario); USD has no operator limit configured.
        var overLimit = await operatorClient.PostIdempotentAsync(Url(zarPayment), new { amount = "1000.01" });
        overLimit.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DashboardClient.ReadJsonAsync(overLimit)).GetProperty("code").GetString().ShouldBe("operator_refund_limit_exceeded");
        (await operatorClient.PostIdempotentAsync(Url(zarPayment), new { amount = "500.00" })).StatusCode.ShouldBe(HttpStatusCode.Created);
        var usd = await operatorClient.PostIdempotentAsync(Url(usdPayment), new { amount = "1.00" });
        (await DashboardClient.ReadJsonAsync(usd)).GetProperty("code").GetString().ShouldBe("operator_refund_not_permitted");
        (await operatorClient.PostIdempotentAsync(Url(zarPayment, "reversal"), new { reason = "no" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);

        // MerchantAdmins have no per-refund limit.
        var admin = await m.Owner.PostIdempotentAsync(Url(zarPayment), new { amount = "1200.00" });
        admin.StatusCode.ShouldBe(HttpStatusCode.Created, await admin.Content.ReadAsStringAsync());
        (await DashboardClient.ReadJsonAsync(admin)).GetProperty("requestedByType").GetString().ShouldBe("User");

        using var platform = await MerchantScenario.AdminAsync(host);
        var byPlatform = await platform.PostIdempotentAsync(Url(zarPayment), new { amount = "1.00" });
        byPlatform.StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await DashboardClient.ReadJsonAsync(byPlatform)).GetProperty("code").GetString().ShouldBe("merchant_users_only");

        var history = await DashboardClient.ReadJsonAsync(await m.Owner.Http.GetAsync(new Uri(Url(zarPayment), UriKind.Relative)));
        history.GetProperty("refunds").EnumerateArray().Select(r => r.GetProperty("amount").GetString()).ShouldBe(["500.00", "1200.00"]);
        var activity = await DashboardClient.ReadJsonAsync(await m.Owner.Http.GetAsync(new Uri($"/api/merchants/{m.Id}/activity", UriKind.Relative)));
        activity.GetProperty("items").EnumerateArray().Count(i => i.GetProperty("action").GetString() == "payment.refund.requested").ShouldBe(2);
    }

    [Fact]
    public async Task Merchants_cannot_refund_each_others_payments()
    {
        using var mine = await PaymentScenario.PayingMerchantAsync(host);
        using var theirs = await PaymentScenario.PayingMerchantAsync(host);
        var theirPayment = await RefundScenario.PaidAsync(theirs.Api, "100.00");

        (await RefundScenario.RefundAsync(mine.Api, theirPayment, "10.00")).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RefundScenario.ReverseAsync(mine.Api, theirPayment)).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        (await RefundScenario.GetPaymentAsync(theirs.Api, theirPayment)).GetProperty("refundableAmount").GetString().ShouldBe("100.00");
    }

    [Fact]
    public async Task Payments_refunds_and_reversals_can_never_be_deleted()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var refunded = await RefundScenario.PaidAsync(m.Api, "100.00");
        (await RefundScenario.RefundAsync(m.Api, refunded, "10.00")).StatusCode.ShouldBe(HttpStatusCode.Created);
        var reversed = await RefundScenario.PaidAsync(m.Api, "100.00");
        (await RefundScenario.ReverseAsync(m.Api, reversed)).StatusCode.ShouldBe(HttpStatusCode.Created);
        var merchant = PaymentScenario.DecodeMerchant(m.Id);

        await using var owner = await host.OpenOwnerConnectionAsync();
        foreach (var sql in new[]
                 {
                     "DELETE FROM payments.payments WHERE merchant_id = @m",
                     "DELETE FROM refunds.refunds WHERE merchant_id = @m",
                     "DELETE FROM refunds.reversals WHERE merchant_id = @m",
                     "UPDATE refunds.reversals SET amount = 1 WHERE merchant_id = @m",
                 })
        {
            await using var command = new NpgsqlCommand(sql, owner);
            command.Parameters.AddWithValue("m", merchant);
            (await Should.ThrowAsync<PostgresException>(() => command.ExecuteNonQueryAsync())).MessageText.ShouldContain("append_only_violation", customMessage: sql);
        }

        await using var app = new NpgsqlConnection(host.AppConnectionString);
        await app.OpenAsync();
        await using var appDelete = new NpgsqlCommand("DELETE FROM refunds.refunds WHERE merchant_id = @m", app);
        appDelete.Parameters.AddWithValue("m", merchant);
        (await Should.ThrowAsync<PostgresException>(() => appDelete.ExecuteNonQueryAsync())).SqlState.ShouldBe(PostgresErrorCodes.InsufficientPrivilege);
    }
}
