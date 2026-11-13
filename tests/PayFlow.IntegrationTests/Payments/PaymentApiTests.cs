using System.Net;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Payments;

public sealed class PaymentApiTests(PayFlowTestHost host)
{
    [Fact]
    public async Task A_successful_payment_charges_the_documented_fee_and_posts_balanced_ledger_entries()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);

        var payment = await PaymentScenario.CreateOkAsync(m.Api, PaymentScenario.Body("100.00"));

        payment.GetProperty("id").GetString()!.ShouldStartWith("pay_");
        payment.GetProperty("object").GetString().ShouldBe("payment");
        payment.GetProperty("status").GetString().ShouldBe("Succeeded");
        payment.GetProperty("amount").GetString().ShouldBe("100.00");
        payment.GetProperty("fee").GetString().ShouldBe("4.90");
        payment.GetProperty("net").GetString().ShouldBe("95.10");
        payment.GetProperty("simulated").GetBoolean().ShouldBeTrue();

        var id = payment.GetProperty("id").GetString()!;
        var detail = await MerchantScenario.ReadAsync(await m.Api.GetAsync(new Uri($"/v1/payments/{id}", UriKind.Relative)));
        detail.GetProperty("statusHistory").EnumerateArray().Select(h => h.GetProperty("to").GetString())
            .ShouldBe(["Created", "Processing", "Succeeded"]);

        var dashboard = await DashboardClient.ReadJsonAsync(await m.Owner.Http.GetAsync(new Uri($"/api/merchants/{m.Id}/payments/{id}", UriKind.Relative)));
        var entries = dashboard.GetProperty("ledger").EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("entryType").GetString()).ShouldBe(["PaymentCaptured", "PaymentFee"]);
        foreach (var entry in entries)
        {
            var postings = entry.GetProperty("postings").EnumerateArray().ToList();
            var debit = postings.Where(p => p.GetProperty("direction").GetString() == "Debit").Sum(p => decimal.Parse(p.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            var credit = postings.Where(p => p.GetProperty("direction").GetString() == "Credit").Sum(p => decimal.Parse(p.GetProperty("amount").GetString()!, System.Globalization.CultureInfo.InvariantCulture));
            debit.ShouldBe(credit);
        }

        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(95.10m);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM platform.outbox_messages WHERE type = 'payment.succeeded' AND payload->>'paymentId' = @id", ("id", id))).ShouldBe(1);
    }

    [Theory]
    [InlineData("sim_card_declined_insufficient_funds", "Declined", "insufficient_funds")]
    [InlineData("sim_card_processor_error", "Failed", "processor_error")]
    public async Task Declined_and_failed_payments_move_no_money(string method, string status, string code)
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);

        var payment = await PaymentScenario.CreateOkAsync(m.Api, PaymentScenario.Body("80.00", method: method));

        payment.GetProperty("status").GetString().ShouldBe(status);
        payment.GetProperty("failureCode").GetString().ShouldBe(code);
        payment.GetProperty("fee").ValueKind.ShouldBe(System.Text.Json.JsonValueKind.Null);
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(0m);

        using var admin = await MerchantScenario.AdminAsync(host);
        var search = await DashboardClient.ReadJsonAsync(await admin.Http.GetAsync(new Uri($"/api/payments?merchantId={m.Id}&failureCode={code}", UriKind.Relative)));
        search.GetProperty("items").EnumerateArray().Single().GetProperty("id").GetString().ShouldBe(payment.GetProperty("id").GetString());
    }

    [Fact]
    public async Task Retrying_with_the_same_key_replays_the_original_response_without_a_new_payment()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var key = Guid.NewGuid().ToString("N");
        var body = PaymentScenario.Body("250.00");

        var first = await PaymentScenario.CreateAsync(m.Api, body, key);
        var retry = await PaymentScenario.CreateAsync(m.Api, body, key);

        first.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.StatusCode.ShouldBe(HttpStatusCode.Created);
        retry.Headers.GetValues("Idempotent-Replayed").Single().ShouldBe("true");
        (await retry.Content.ReadAsStringAsync()).ShouldBe(await first.Content.ReadAsStringAsync());
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM payments.payments WHERE merchant_id = @m",
            ("m", PaymentScenario.DecodeMerchant(m.Id)))).ShouldBe(1);
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(240.75m);
    }

    [Fact]
    public async Task Reusing_a_key_with_a_different_payload_is_rejected()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var key = Guid.NewGuid().ToString("N");
        (await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body("10.00", reference: "order-a"), key)).StatusCode.ShouldBe(HttpStatusCode.Created);

        var conflicting = await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body("10.01", reference: "order-a"), key);

        conflicting.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MerchantScenario.ReadAsync(conflicting)).GetProperty("code").GetString().ShouldBe("idempotency_key_reused");
    }

    [Fact]
    public async Task Fifty_concurrent_identical_requests_create_exactly_one_payment()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var key = Guid.NewGuid().ToString("N");
        var body = PaymentScenario.Body("19.99", reference: "concurrent-order");

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(_ => PaymentScenario.CreateAsync(m.Api, body, key)));

        responses.ShouldAllBe(r => r.StatusCode == HttpStatusCode.Created || r.StatusCode == HttpStatusCode.Conflict);
        var created = responses.Where(r => r.StatusCode == HttpStatusCode.Created).ToList();
        created.ShouldNotBeEmpty();
        var ids = new HashSet<string>();
        foreach (var r in created)
        {
            ids.Add((await MerchantScenario.ReadAsync(r)).GetProperty("id").GetString()!);
        }

        ids.Count.ShouldBe(1);
        foreach (var r in responses.Where(r => r.StatusCode == HttpStatusCode.Conflict))
        {
            (await MerchantScenario.ReadAsync(r)).GetProperty("code").GetString().ShouldBe("idempotency_request_in_progress");
            r.Headers.RetryAfter.ShouldNotBeNull();
        }

        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM payments.payments WHERE merchant_id = @m",
            ("m", PaymentScenario.DecodeMerchant(m.Id)))).ShouldBe(1);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM ledger.journal_entries WHERE source_id = @p AND entry_type = 'PaymentCaptured'",
            ("p", PaymentScenario.DecodePayment(ids.Single())))).ShouldBe(1);
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(17.41m);
    }

    [Fact]
    public async Task Fifty_concurrent_distinct_payments_keep_the_ledger_consistent()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);

        var responses = await Task.WhenAll(Enumerable.Range(0, 50).Select(i =>
            PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body("10.00", reference: $"bulk-{i}"), Guid.NewGuid().ToString("N"))));

        var failures = new List<string>();
        foreach (var r in responses.Where(r => r.StatusCode != HttpStatusCode.Created))
        {
            failures.Add($"{(int)r.StatusCode}: {await r.Content.ReadAsStringAsync()}");
        }

        failures.ShouldBeEmpty();
        // Each ZAR 10.00 payment: fee 0.29 + 2.00 = 2.29, net 7.71.
        (await PaymentScenario.MerchantPayableAsync(host, m.Id, "ZAR")).ShouldBe(50 * 7.71m);
        (await PaymentScenario.ScalarAsync(host,
            """
            SELECT count(*) FROM (
              SELECT p.journal_entry_id
                FROM ledger.postings p
               GROUP BY p.journal_entry_id
              HAVING sum(CASE WHEN p.direction = 'Debit' THEN p.amount ELSE -p.amount END) <> 0) unbalanced
            """)).ShouldBe(0);
    }

    [Theory]
    [InlineData(null, "idempotency_key_required", 400)]
    [InlineData("has spaces", "idempotency_key_invalid", 400)]
    public async Task An_idempotency_key_is_required(string? key, string code, int status)
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);

        var response = await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body(), key);

        ((int)response.StatusCode).ShouldBe(status);
        (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe(code);
    }

    [Theory]
    [InlineData("10.001", "ZAR", "sim_card_success", 400, "invalid_amount")]
    [InlineData("4.99", "ZAR", "sim_card_success", 422, "amount_below_minimum")]
    [InlineData("50000.01", "ZAR", "sim_card_success", 422, "amount_above_maximum")]
    [InlineData("10.00", "EUR", "sim_card_success", 422, "currency_not_enabled")]
    [InlineData("10.00", "ZAR", "4111111111111111", 400, "validation_failed")]
    [InlineData("10.00", "ZAR", "visa", 400, "validation_failed")]
    public async Task Invalid_payments_are_rejected_and_the_key_is_released(string amount, string currency, string method, int status, string code)
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var key = Guid.NewGuid().ToString("N");

        var rejected = await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body(amount, currency, method, reference: "fix-me"), key);

        ((int)rejected.StatusCode).ShouldBe(status);
        (await MerchantScenario.ReadAsync(rejected)).GetProperty("code").GetString().ShouldBe(code);

        // Nothing was executed, so the same key can be used once the request is corrected.
        var corrected = await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body("10.00", "ZAR", reference: "fix-me"), key);
        corrected.StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Card_numbers_are_refused_in_every_field_and_never_stored()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        const string pan = "4111 1111 1111 1111";

        var response = await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body(description: $"Card {pan}"), Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe("card_data_not_accepted");
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM payments.payments WHERE description LIKE '%4111%'")).ShouldBe(0);
        host.Factory.Logs.Contains("4111 1111 1111 1111").ShouldBeFalse();
    }

    [Fact]
    public async Task Suspended_merchants_cannot_take_payments()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        using var admin = await MerchantScenario.AdminAsync(host);
        (await admin.PostAsync($"/api/merchants/{m.Id}/status", new { status = "Suspended", reason = "Risk review" })).StatusCode.ShouldBe(HttpStatusCode.OK);

        var response = await PaymentScenario.CreateAsync(m.Api, PaymentScenario.Body(), Guid.NewGuid().ToString("N"));

        response.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await MerchantScenario.ReadAsync(response)).GetProperty("code").GetString().ShouldBe("merchant_not_active");
    }

    [Fact]
    public async Task Merchants_only_ever_see_their_own_payments()
    {
        using var mine = await PaymentScenario.PayingMerchantAsync(host);
        using var theirs = await PaymentScenario.PayingMerchantAsync(host);
        var theirPayment = (await PaymentScenario.CreateOkAsync(theirs.Api, PaymentScenario.Body())).GetProperty("id").GetString();

        (await mine.Api.GetAsync(new Uri($"/v1/payments/{theirPayment}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
        var list = await MerchantScenario.ReadAsync(await mine.Api.GetAsync(new Uri("/v1/payments", UriKind.Relative)));
        list.GetProperty("data").EnumerateArray().ShouldBeEmpty();
        (await mine.Owner.Http.GetAsync(new Uri($"/api/merchants/{theirs.Id}/payments", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await mine.Owner.Http.GetAsync(new Uri($"/api/merchants/{mine.Id}/payments/{theirPayment}", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Payment_lists_page_with_a_cursor()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var created = new List<string>();
        for (var i = 0; i < 3; i++)
        {
            created.Add((await PaymentScenario.CreateOkAsync(m.Api, PaymentScenario.Body(reference: $"page-{i}"))).GetProperty("id").GetString()!);
        }

        var first = await MerchantScenario.ReadAsync(await m.Api.GetAsync(new Uri("/v1/payments?limit=2", UriKind.Relative)));
        first.GetProperty("hasMore").GetBoolean().ShouldBeTrue();
        var cursor = first.GetProperty("nextStartingAfter").GetString();
        var second = await MerchantScenario.ReadAsync(await m.Api.GetAsync(new Uri($"/v1/payments?limit=2&startingAfter={cursor}", UriKind.Relative)));
        second.GetProperty("hasMore").GetBoolean().ShouldBeFalse();

        var seen = first.GetProperty("data").EnumerateArray().Concat(second.GetProperty("data").EnumerateArray()).Select(p => p.GetProperty("id").GetString()!).ToList();
        seen.ShouldBe(Enumerable.Reverse(created).ToList()); // newest first, no gaps or repeats
    }

    [Fact]
    public async Task The_database_rejects_unbalanced_or_edited_ledger_entries_and_over_refunds()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);
        var paymentId = PaymentScenario.DecodePayment((await PaymentScenario.CreateOkAsync(m.Api, PaymentScenario.Body())).GetProperty("id").GetString()!);
        await using var db = await host.OpenOwnerConnectionAsync();

        // An entry whose debits ≠ credits fails at commit (deferred constraint trigger).
        await using (var tx = await db.BeginTransactionAsync())
        {
            await using var insert = new NpgsqlCommand(
                """
                INSERT INTO ledger.journal_entries (id, entry_type, source_type, source_id, currency, description, occurred_at, correlation_id)
                VALUES (gen_random_uuid(), 'Manual', 'test', gen_random_uuid(), 'ZAR', 'unbalanced', now(), 'test') RETURNING id
                """, db, tx);
            var entryId = (Guid)(await insert.ExecuteScalarAsync())!;
            await using var posting = new NpgsqlCommand(
                "INSERT INTO ledger.postings (journal_entry_id, account_id, direction, amount) SELECT @e, id, 'Debit', 5 FROM ledger.accounts WHERE code = 'fee_revenue:ZAR'", db, tx);
            posting.Parameters.AddWithValue("e", entryId);
            await posting.ExecuteNonQueryAsync();
            (await Should.ThrowAsync<PostgresException>(() => tx.CommitAsync())).MessageText.ShouldContain("ledger_violation");
        }

        await using var edit = new NpgsqlCommand("UPDATE ledger.postings SET amount = amount + 1 WHERE journal_entry_id IN (SELECT id FROM ledger.journal_entries WHERE source_id = @p)", db);
        edit.Parameters.AddWithValue("p", paymentId);
        (await Should.ThrowAsync<PostgresException>(() => edit.ExecuteNonQueryAsync())).MessageText.ShouldContain("append_only_violation");

        await using var overRefund = new NpgsqlCommand("UPDATE payments.payments SET refunded_amount = amount + 0.01 WHERE id = @p", db);
        overRefund.Parameters.AddWithValue("p", paymentId);
        (await Should.ThrowAsync<PostgresException>(() => overRefund.ExecuteNonQueryAsync())).ConstraintName.ShouldBe("ck_payments_refund_balance");

        await using var history = new NpgsqlCommand("DELETE FROM payments.payment_status_history WHERE payment_id = @p", db);
        history.Parameters.AddWithValue("p", paymentId);
        (await Should.ThrowAsync<PostgresException>(() => history.ExecuteNonQueryAsync())).MessageText.ShouldContain("append_only_violation");
    }
}
