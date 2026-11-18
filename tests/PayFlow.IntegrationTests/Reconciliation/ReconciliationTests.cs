using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using PayFlow.IntegrationTests.Infrastructure;

namespace PayFlow.IntegrationTests.Reconciliation;

/// <summary>
/// A statement covers every payment in its period, across merchants. Each test therefore puts its
/// payments on a day of its own, years ahead, so tests sharing the database never see each other's.
/// </summary>
public sealed class ReconciliationTests(PayFlowTestHost host)
{
    [Fact]
    public async Task A_generated_statement_produces_every_outcome_and_a_rerun_creates_no_new_exceptions()
    {
        var (factory, day) = Host(dayOffset: 1100);
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        for (var i = 0; i < 7; i++)
        {
            await RefundScenario.PaidAsync(m.Api, $"{10 + i}.00");
        }

        var reversed = await RefundScenario.PaidAsync(m.Api, "42.00");
        (await RefundScenario.ReverseAsync(m.Api, reversed)).StatusCode.ShouldBe(HttpStatusCode.Created);

        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var generated = await GenerateAsync(admin, day, new
        {
            missingExternal = 1,
            missingInternal = 1,
            amountMismatch = 1,
            currencyMismatch = 1,
            duplicateExternal = 1,
            statusDiscrepancy = 1,
        });
        generated.GetProperty("injected").GetArrayLength().ShouldBe(6);
        generated.GetProperty("lineCount").GetInt32().ShouldBe(9); // 8 payments − 1 missing + 1 duplicate + 1 unknown

        var statement = await ImportAsync(admin, generated);
        statement.GetProperty("id").GetString()!.ShouldStartWith("stmt_");
        statement.GetProperty("lineCount").GetInt32().ShouldBe(9);
        statement.GetProperty("status").GetString().ShouldBe("Imported");

        var again = await admin.PostAsync("/api/reconciliation/statements", ImportBody(generated));
        again.StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await DashboardClient.ReadJsonAsync(again)).GetProperty("code").GetString().ShouldBe("statement_already_imported");

        var statementId = statement.GetProperty("id").GetString()!;
        var run = await RunAsync(admin, statementId);
        var counts = run.GetProperty("counts");
        counts.GetProperty("matched").GetInt32().ShouldBe(4); // 3 faithful + the first of the duplicated pair
        counts.GetProperty("missingExternal").GetInt32().ShouldBe(1);
        counts.GetProperty("missingInternal").GetInt32().ShouldBe(1);
        counts.GetProperty("amountMismatch").GetInt32().ShouldBe(1);
        counts.GetProperty("currencyMismatch").GetInt32().ShouldBe(1);
        counts.GetProperty("duplicateExternal").GetInt32().ShouldBe(1);
        counts.GetProperty("statusDiscrepancy").GetInt32().ShouldBe(1);
        run.GetProperty("exceptionsCreated").GetInt32().ShouldBe(6);

        // Each planted discrepancy is found against the record it was planted on.
        var exceptions = await ExceptionsAsync(admin, statementId);
        exceptions.GetProperty("total").GetInt32().ShouldBe(6);
        foreach (var planted in generated.GetProperty("injected").EnumerateArray())
        {
            exceptions.GetProperty("items").EnumerateArray().ShouldContain(x =>
                x.GetProperty("outcome").GetString() == planted.GetProperty("outcome").GetString() &&
                x.GetProperty("reference").GetString() == planted.GetProperty("reference").GetString());
        }

        var amount = exceptions.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("outcome").GetString() == "AmountMismatch");
        (decimal.Parse(amount.GetProperty("externalAmount").GetString()!, System.Globalization.CultureInfo.InvariantCulture) -
         decimal.Parse(amount.GetProperty("internalAmount").GetString()!, System.Globalization.CultureInfo.InvariantCulture)).ShouldBe(1m);

        var rerun = await RunAsync(admin, statementId);
        rerun.GetProperty("exceptionsCreated").GetInt32().ShouldBe(0);
        rerun.GetProperty("exceptionsSeenAgain").GetInt32().ShouldBe(6);
        rerun.GetProperty("counts").ToString().ShouldBe(counts.ToString());
        var afterRerun = await ExceptionsAsync(admin, statementId);
        afterRerun.GetProperty("total").GetInt32().ShouldBe(6);
        afterRerun.GetProperty("items").EnumerateArray().ShouldAllBe(x => x.GetProperty("timesSeen").GetInt32() == 2);

        // Both runs keep their own evidence.
        var results = await DashboardClient.ReadJsonAsync(await admin.Http.GetAsync(
            new Uri($"/api/reconciliation/runs/{run.GetProperty("id").GetString()}/results?outcome=DuplicateExternal", UriKind.Relative)));
        results.GetProperty("total").GetInt32().ShouldBe(1);
        results.GetProperty("items")[0].GetProperty("lineNumber").GetInt32().ShouldBeGreaterThan(1);
        var reloaded = await DashboardClient.ReadJsonAsync(await admin.Http.GetAsync(new Uri($"/api/reconciliation/statements/{statementId}", UriKind.Relative)));
        reloaded.GetProperty("status").GetString().ShouldBe("Reconciled");
        reloaded.GetProperty("lastRunId").GetString().ShouldBe(rerun.GetProperty("id").GetString());
    }

    [Fact]
    public async Task A_faithful_statement_matches_everything_including_reversals()
    {
        var (factory, day) = Host(dayOffset: 1101);
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        await RefundScenario.PaidAsync(m.Api, "12.34");
        await RefundScenario.PaidAsync(m.Api, "99.99", currency: "USD");
        var reversed = await RefundScenario.PaidAsync(m.Api, "50.00");
        (await RefundScenario.ReverseAsync(m.Api, reversed)).StatusCode.ShouldBe(HttpStatusCode.Created);
        await RefundScenario.PaidAsync(m.Api, "10.00", method: "sim_card_declined_insufficient_funds"); // never reaches the acquirer's books

        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var statement = await ImportAsync(admin, await GenerateAsync(admin, day, null));
        var run = await RunAsync(admin, statement.GetProperty("id").GetString()!);

        run.GetProperty("counts").GetProperty("matched").GetInt32().ShouldBe(3);
        run.GetProperty("exceptionsCreated").GetInt32().ShouldBe(0);
    }

    [Fact]
    public async Task Exceptions_move_through_review_to_resolution_and_an_adjustment_posts_to_the_ledger_once()
    {
        var (factory, day) = Host(dayOffset: 1102);
        await using var _ = factory;
        using var m = await PaymentScenario.PayingMerchantAsync(host, factory);
        await RefundScenario.PaidAsync(m.Api, "100.00");
        await RefundScenario.PaidAsync(m.Api, "200.00");
        using var admin = await MerchantScenario.AdminAsync(host, factory);
        var statement = await ImportAsync(admin, await GenerateAsync(admin, day, new { amountMismatch = 1, missingInternal = 1 }));
        var statementId = statement.GetProperty("id").GetString()!;
        await RunAsync(admin, statementId);
        var items = (await ExceptionsAsync(admin, statementId)).GetProperty("items").EnumerateArray().ToList();
        var mismatch = items.Single(x => x.GetProperty("outcome").GetString() == "AmountMismatch").GetProperty("id").GetString()!;
        var unknown = items.Single(x => x.GetProperty("outcome").GetString() == "MissingInternal").GetProperty("id").GetString()!;

        var review = await admin.PostAsync($"/api/reconciliation/exceptions/{mismatch}/review", new { note = "Looking into it" });
        review.StatusCode.ShouldBe(HttpStatusCode.OK, await review.Content.ReadAsStringAsync());
        (await DashboardClient.ReadJsonAsync(review)).GetProperty("status").GetString().ShouldBe("InReview");
        (await admin.PostAsync($"/api/reconciliation/exceptions/{mismatch}/review", new { note = "again" })).StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);

        (await admin.PostAsync($"/api/reconciliation/exceptions/{mismatch}/resolve", new { note = "no type" })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await admin.PostAsync($"/api/reconciliation/exceptions/{mismatch}/resolve", new { resolutionType = "AdjustmentPosted", note = "x", adjustment = new { amount = "1.001", direction = "IncreaseReceivable" } }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        var resolved = await admin.PostAsync($"/api/reconciliation/exceptions/{mismatch}/resolve", new
        {
            resolutionType = "AdjustmentPosted",
            note = "Acquirer captured one rand more; booked to suspense pending their correction.",
            adjustment = new { amount = "1.00", direction = "IncreaseReceivable" },
        });
        resolved.StatusCode.ShouldBe(HttpStatusCode.OK, await resolved.Content.ReadAsStringAsync());
        var body = await DashboardClient.ReadJsonAsync(resolved);
        body.GetProperty("status").GetString().ShouldBe("Resolved");
        body.GetProperty("resolutionType").GetString().ShouldBe("AdjustmentPosted");
        body.GetProperty("adjustmentAmount").GetString().ShouldBe("1.00");
        body.GetProperty("history").EnumerateArray().Select(h => h.GetProperty("to").GetString()).ShouldBe(["Open", "InReview", "Resolved"]);

        var exceptionGuid = Decode(mismatch);
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM ledger.journal_entries WHERE entry_type = 'ReconciliationAdjustment' AND source_type = 'reconciliation_exception' AND source_id = @x",
            ("x", exceptionGuid))).ShouldBe(1);
        (await PaymentScenario.DecimalAsync(host,
            """
            SELECT sum(CASE WHEN p.direction = 'Debit' THEN p.amount ELSE -p.amount END)
              FROM ledger.postings p JOIN ledger.accounts a ON a.id = p.account_id JOIN ledger.journal_entries e ON e.id = p.journal_entry_id
             WHERE e.source_id = @x AND a.kind = 'AcquirerReceivable'
            """, ("x", exceptionGuid))).ShouldBe(1.00m);

        var twice = await admin.PostAsync($"/api/reconciliation/exceptions/{mismatch}/resolve", new { resolutionType = "AcceptedInternal", note = "again" });
        twice.StatusCode.ShouldBe(HttpStatusCode.UnprocessableEntity);
        (await DashboardClient.ReadJsonAsync(twice)).GetProperty("code").GetString().ShouldBe("invalid_exception_transition");

        // Dismissal needs a note; a dismissed exception records it as a false positive.
        (await admin.PostAsync($"/api/reconciliation/exceptions/{unknown}/dismiss", new { note = (string?)null })).StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var dismissed = await DashboardClient.ReadJsonAsync(await admin.PostAsync($"/api/reconciliation/exceptions/{unknown}/dismiss", new { note = "Test transaction from the acquirer's sandbox" }));
        dismissed.GetProperty("status").GetString().ShouldBe("Dismissed");
        dismissed.GetProperty("resolutionType").GetString().ShouldBe("FalsePositive");

        // A rerun leaves closed exceptions closed.
        (await RunAsync(admin, statementId)).GetProperty("exceptionsCreated").GetInt32().ShouldBe(0);
        var after = await DashboardClient.ReadJsonAsync(await admin.Http.GetAsync(new Uri($"/api/reconciliation/exceptions/{mismatch}", UriKind.Relative)));
        after.GetProperty("status").GetString().ShouldBe("Resolved");
        after.GetProperty("timesSeen").GetInt32().ShouldBe(2);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM ledger.journal_entries WHERE source_id = @x", ("x", exceptionGuid))).ShouldBe(1);
    }

    [Fact]
    public async Task A_malformed_statement_is_rejected_whole_with_every_line_error()
    {
        using var admin = await MerchantScenario.AdminAsync(host);
        var csv = """
            external_id,payflow_reference,type,amount,currency,status,value_date
            EXT-1,pay_1,PAYMENT,10.00,ZAR,CAPTURED,2026-11-17
            EXT-2,pay_2,PAYMENT,ten,ZAR,CAPTURED,2026-11-17
            EXT-3,re_3,REFUND,5.00,ZAR,CAPTURED,2026-11-17
            """;
        var response = await admin.PostAsync("/api/reconciliation/statements", new
        {
            sourceName = "Simulated Acquirer",
            periodStart = "2026-11-17",
            periodEnd = "2026-11-17",
            fileName = $"bad-{Guid.NewGuid():N}.csv",
            csv,
        });

        response.StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        var problem = await DashboardClient.ReadJsonAsync(response);
        problem.GetProperty("code").GetString().ShouldBe("invalid_statement");
        problem.GetProperty("errors").EnumerateObject().Select(p => p.Name).ShouldBe(["line 3", "line 4"], ignoreOrder: true);
        (await PaymentScenario.ScalarAsync(host, "SELECT count(*) FROM reconciliation.statement_lines WHERE external_id = 'EXT-1' AND reference = 'pay_1'")).ShouldBe(0);
    }

    [Fact]
    public async Task Reconciliation_is_for_platform_admins_only()
    {
        using var m = await PaymentScenario.PayingMerchantAsync(host);

        (await m.Owner.Http.GetAsync(new Uri("/api/reconciliation/exceptions", UriKind.Relative))).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await m.Owner.PostAsync("/api/reconciliation/statements/generate", new { periodStart = "2026-11-17", periodEnd = "2026-11-17" })).StatusCode.ShouldBe(HttpStatusCode.Forbidden);
        (await m.Api.GetAsync(new Uri("/api/reconciliation/statements", UriKind.Relative))).StatusCode.ShouldBeOneOf(HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Imported_statement_lines_cannot_be_rewritten_even_by_the_owner()
    {
        await using var db = await host.OpenOwnerConnectionAsync();
        await using var transaction = await db.BeginTransactionAsync();
        await using var seed = new NpgsqlCommand(
            """
            WITH s AS (
                INSERT INTO reconciliation.statements (id, source_name, period_start, period_end, file_name, file_sha256, line_count, imported_by, imported_at, status)
                VALUES (gen_random_uuid(), 't', current_date, current_date, 't.csv', sha256(gen_random_uuid()::text::bytea), 1, 't', now(), 'Imported')
                RETURNING id)
            INSERT INTO reconciliation.statement_lines (statement_id, line_number, external_id, reference, transaction_type, amount, currency, external_status, value_date, raw)
            SELECT id, 2, 'E', 'R', 'PAYMENT', 1, 'ZAR', 'CAPTURED', current_date, '' FROM s
            RETURNING statement_id
            """, db, transaction);
        var statementId = (Guid)(await seed.ExecuteScalarAsync())!;

        await using var tamper = new NpgsqlCommand("UPDATE reconciliation.statement_lines SET amount = 1000 WHERE statement_id = @s", db, transaction);
        tamper.Parameters.AddWithValue("s", statementId);
        var error = await Should.ThrowAsync<PostgresException>(() => tamper.ExecuteNonQueryAsync());

        error.MessageText.ShouldContain("append_only_violation");
    }

    [Theory]
    [InlineData("settlement", "items", "append_only")]
    [InlineData("settlement", "batches", "no_delete")]
    [InlineData("reconciliation", "statement_lines", "append_only")]
    [InlineData("reconciliation", "results", "append_only")]
    [InlineData("reconciliation", "exception_history", "append_only")]
    [InlineData("reconciliation", "statements", "no_delete")]
    [InlineData("reconciliation", "runs", "no_delete")]
    [InlineData("reconciliation", "exceptions", "no_delete")]
    public async Task Settlement_and_reconciliation_tables_are_protected(string schema, string table, string guard) =>
        (await PaymentScenario.ScalarAsync(host,
            "SELECT count(*) FROM pg_trigger t JOIN pg_class c ON c.oid = t.tgrelid JOIN pg_namespace n ON n.oid = c.relnamespace WHERE n.nspname = @s AND c.relname = @t AND t.tgname = @g",
            ("s", schema), ("t", table), ("g", $"trg_{table}_{guard}"))).ShouldBe(1);

    private (PayFlowApiFactory Factory, DateOnly Day) Host(int dayOffset)
    {
        var start = new DateTimeOffset(DateTime.UtcNow.Date.AddDays(dayOffset).AddHours(10), TimeSpan.Zero);
        var clock = new FakeTimeProvider(start);
        return (host.CreateFactory(clock: clock), DateOnly.FromDateTime(start.UtcDateTime));
    }

    private static async Task<JsonElement> GenerateAsync(DashboardClient admin, DateOnly day, object? inject)
    {
        var response = await admin.PostAsync("/api/reconciliation/statements/generate", new { periodStart = day, periodEnd = day, inject });
        response.StatusCode.ShouldBe(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return await DashboardClient.ReadJsonAsync(response);
    }

    private static object ImportBody(JsonElement generated) => new
    {
        sourceName = generated.GetProperty("sourceName").GetString(),
        periodStart = generated.GetProperty("periodStart").GetString(),
        periodEnd = generated.GetProperty("periodEnd").GetString(),
        fileName = generated.GetProperty("fileName").GetString(),
        csv = generated.GetProperty("csv").GetString(),
    };

    private static async Task<JsonElement> ImportAsync(DashboardClient admin, JsonElement generated)
    {
        var response = await admin.PostAsync("/api/reconciliation/statements", ImportBody(generated));
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await DashboardClient.ReadJsonAsync(response);
    }

    private static async Task<JsonElement> RunAsync(DashboardClient admin, string statementId)
    {
        var response = await admin.PostAsync($"/api/reconciliation/statements/{statementId}/runs", null);
        response.StatusCode.ShouldBe(HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return await DashboardClient.ReadJsonAsync(response);
    }

    private static async Task<JsonElement> ExceptionsAsync(DashboardClient admin, string statementId) =>
        await DashboardClient.ReadJsonAsync(await admin.Http.GetAsync(new Uri($"/api/reconciliation/exceptions?statementId={statementId}&pageSize=100", UriKind.Relative)));

    private static Guid Decode(string exceptionId) =>
        PayFlow.SharedKernel.Ids.PublicId.TryParse(exceptionId, "rex", out var id) ? id.Value : throw new ArgumentException(exceptionId);
}
