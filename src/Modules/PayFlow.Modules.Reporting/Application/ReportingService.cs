using System.Data.Common;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using PayFlow.Infrastructure.Persistence;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using StackExchange.Redis;

namespace PayFlow.Modules.Reporting.Application;

public sealed record ReportQuery(Guid? MerchantId, string Currency, DateOnly From, DateOnly To, bool IncludeReconciliation);

public sealed record DailyVolume(DateOnly Date, int Payments, int Succeeded, string SucceededAmount);

/// <summary>RPT-1: payments created in the window, per UTC day.</summary>
public sealed record VolumeSection(int Payments, int Succeeded, string SucceededAmount, IReadOnlyList<DailyVolume> Days);

public sealed record FailureCodeCount(string Code, int Count);

/// <summary>RPT-2: final outcomes of payments created in the window. Rates are fractions of finished payments.</summary>
public sealed record OutcomeSection(
    int Succeeded,
    int Reversed,
    int Declined,
    int Failed,
    int Expired,
    int InFlight,
    decimal SuccessRate,
    decimal DeclineRate,
    decimal FailureRate,
    IReadOnlyList<FailureCodeCount> TopFailureCodes);

/// <summary>RPT-3: refunds that succeeded in the window; the rate is refunded value over captured value.</summary>
public sealed record RefundSection(int Count, string Amount, int Failed, decimal RefundRate);

/// <summary>RPT-4: settlement batches created in the window.</summary>
public sealed record SettlementSection(int Batches, int Pending, int Paid, int Failed, string PaidOut, string AwaitingPayout, string CarriedForward);

public sealed record OutcomeAge(string Outcome, int Open);

public sealed record AgeBucket(string Label, int Open);

/// <summary>RPT-5: unresolved reconciliation exceptions (Open or InReview) now, by type and age. Platform-wide.</summary>
public sealed record ReconciliationSection(int Unresolved, IReadOnlyList<OutcomeAge> ByOutcome, IReadOnlyList<AgeBucket> ByAge);

/// <summary>RPT-6: synchronous processing latency and webhook delivery attempts in the window.</summary>
public sealed record PerformanceSection(
    int ProcessedPayments,
    long? ProcessingP50Ms,
    long? ProcessingP95Ms,
    int WebhookAttempts,
    decimal WebhookSuccessRate,
    long? WebhookP50Ms,
    long? WebhookP95Ms);

public sealed record ReportResponse(
    string? MerchantId,
    string Currency,
    DateOnly From,
    DateOnly To,
    DateTimeOffset GeneratedAt,
    VolumeSection Volume,
    OutcomeSection Outcomes,
    RefundSection Refunds,
    SettlementSection Settlements,
    ReconciliationSection? Reconciliation,
    PerformanceSection Performance);

/// <summary>
/// Reports (RPT-1..6) as read-only SQL over the module schemas, cached in Redis for
/// <see cref="CacheDuration"/>. Reporting owns no tables and writes nothing; it is the one module
/// allowed to read other schemas directly (ADR 021). Redis is an optimisation: when it is down,
/// every request is computed from PostgreSQL.
/// </summary>
internal sealed class ReportingService(PayFlowDbContext db, IConnectionMultiplexer redis, TimeProvider clock, ILogger<ReportingService> logger)
{
    public static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);
    public const int MaxDays = 366;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static Result<ReportQuery> Validate(Guid? merchantId, string? currency, DateOnly? from, DateOnly? to, bool includeReconciliation, DateTimeOffset now)
    {
        var code = string.IsNullOrWhiteSpace(currency) ? Currency.ZAR.Code : currency.Trim().ToUpperInvariant();
        if (!Currency.TryFromCode(code, out _))
        {
            return Error.Validation("unsupported_currency", $"Currency '{currency}' is not supported.");
        }

        var today = DateOnly.FromDateTime(now.UtcDateTime);
        var end = to ?? today;
        var start = from ?? end.AddDays(-29);
        if (end < start)
        {
            return Error.Validation("invalid_period", "'to' must not be before 'from'.");
        }

        if (end.DayNumber - start.DayNumber + 1 > MaxDays)
        {
            return Error.Validation("invalid_period", $"A report covers at most {MaxDays} days.");
        }

        return new ReportQuery(merchantId, code, start, end, includeReconciliation);
    }

    public async Task<ReportResponse> GetAsync(ReportQuery query, CancellationToken ct)
    {
        var key = CacheKey(query);
        var cached = await ReadCacheAsync(key);
        if (cached is not null)
        {
            return cached;
        }

        var report = await ComputeAsync(query, ct);
        await WriteCacheAsync(key, report);
        return report;
    }

    internal static string CacheKey(ReportQuery q) =>
        string.Create(CultureInfo.InvariantCulture,
            $"payflow:report:v1:{(q.MerchantId is { } m ? m.ToString("N") : "all")}:{q.Currency}:{q.From:yyyyMMdd}:{q.To:yyyyMMdd}:{(q.IncludeReconciliation ? "rec" : "norec")}");

    private async Task<ReportResponse> ComputeAsync(ReportQuery q, CancellationToken ct)
    {
        var from = new DateTimeOffset(q.From.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = new DateTimeOffset(q.To.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var currency = Currency.FromCode(q.Currency);
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var opened = connection.State != System.Data.ConnectionState.Open;
        if (opened)
        {
            await connection.OpenAsync(ct);
        }

        try
        {
            var parameters = new Dictionary<string, object?>
            {
                ["merchant"] = q.MerchantId,
                ["currency"] = q.Currency,
                ["from"] = from,
                ["to"] = to,
            };

            // RPT-1 — every UTC day in the window, including days with nothing.
            var days = await QueryAsync(connection, """
                SELECT (d AT TIME ZONE 'UTC')::date AS day,
                       count(p.id) AS payments,
                       count(p.id) FILTER (WHERE p.status IN ('Succeeded','PartiallyRefunded','Refunded','Reversed')) AS succeeded,
                       coalesce(sum(p.amount) FILTER (WHERE p.status IN ('Succeeded','PartiallyRefunded','Refunded','Reversed')), 0) AS succeeded_amount
                  FROM generate_series(@from, @to - interval '1 day', interval '1 day') AS d
                  LEFT JOIN payments.payments p
                         ON p.created_at >= d AND p.created_at < d + interval '1 day'
                        AND p.currency = @currency
                        AND (@merchant::uuid IS NULL OR p.merchant_id = @merchant)
                 GROUP BY d ORDER BY d
                """, parameters, r => new
            {
                Day = DateOnly.FromDateTime(r.GetDateTime(0)),
                Payments = Convert.ToInt32(r.GetInt64(1)),
                Succeeded = Convert.ToInt32(r.GetInt64(2)),
                Amount = r.GetDecimal(3),
            }, ct);

            // RPT-2
            var outcome = (await QueryAsync(connection, """
                SELECT count(*) FILTER (WHERE status IN ('Succeeded','PartiallyRefunded','Refunded')),
                       count(*) FILTER (WHERE status = 'Reversed'),
                       count(*) FILTER (WHERE status = 'Declined'),
                       count(*) FILTER (WHERE status = 'Failed'),
                       count(*) FILTER (WHERE status = 'Expired'),
                       count(*) FILTER (WHERE status IN ('Created','Processing','Pending'))
                  FROM payments.payments
                 WHERE created_at >= @from AND created_at < @to AND currency = @currency
                   AND (@merchant::uuid IS NULL OR merchant_id = @merchant)
                """, parameters, r => new long[] { r.GetInt64(0), r.GetInt64(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4), r.GetInt64(5) }, ct))[0];
            var failureCodes = await QueryAsync(connection, """
                SELECT failure_code, count(*) AS n
                  FROM payments.payments
                 WHERE created_at >= @from AND created_at < @to AND currency = @currency
                   AND (@merchant::uuid IS NULL OR merchant_id = @merchant)
                   AND failure_code IS NOT NULL
                 GROUP BY failure_code ORDER BY n DESC, failure_code LIMIT 5
                """, parameters, r => new FailureCodeCount(r.GetString(0), Convert.ToInt32(r.GetInt64(1))), ct);

            // RPT-3
            var refunds = (await QueryAsync(connection, """
                SELECT count(*) FILTER (WHERE status = 'Succeeded'),
                       coalesce(sum(amount) FILTER (WHERE status = 'Succeeded'), 0),
                       count(*) FILTER (WHERE status = 'Failed')
                  FROM refunds.refunds
                 WHERE completed_at >= @from AND completed_at < @to AND currency = @currency
                   AND (@merchant::uuid IS NULL OR merchant_id = @merchant)
                """, parameters, r => (Count: r.GetInt64(0), Amount: r.GetDecimal(1), Failed: r.GetInt64(2)), ct))[0];

            // RPT-4
            var settlements = (await QueryAsync(connection, """
                SELECT count(*),
                       count(*) FILTER (WHERE status = 'Pending'),
                       count(*) FILTER (WHERE status = 'Paid'),
                       count(*) FILTER (WHERE status = 'Failed'),
                       coalesce(sum(payout_amount) FILTER (WHERE status = 'Paid'), 0),
                       coalesce(sum(payout_amount) FILTER (WHERE status <> 'Paid'), 0),
                       coalesce(sum(carried_forward_out), 0)
                  FROM settlement.batches
                 WHERE created_at >= @from AND created_at < @to AND currency = @currency
                   AND (@merchant::uuid IS NULL OR merchant_id = @merchant)
                """, parameters, r => (Batches: r.GetInt64(0), Pending: r.GetInt64(1), Paid: r.GetInt64(2), Failed: r.GetInt64(3),
                    PaidOut: r.GetDecimal(4), Awaiting: r.GetDecimal(5), Carried: r.GetDecimal(6)), ct))[0];

            // RPT-6 — the synchronous path only: payments that went to Pending wait for a simulated
            // resolution, which would measure the simulator's delay rather than PayFlow.
            var processing = (await QueryAsync(connection, """
                SELECT count(*),
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY ms)::numeric,
                       percentile_cont(0.95) WITHIN GROUP (ORDER BY ms)::numeric
                  FROM (SELECT extract(epoch FROM (completed_at - processing_started_at)) * 1000 AS ms
                          FROM payments.payments
                         WHERE completed_at >= @from AND completed_at < @to AND currency = @currency
                           AND (@merchant::uuid IS NULL OR merchant_id = @merchant)
                           AND processing_started_at IS NOT NULL AND simulated_resolve_at IS NULL) t
                """, parameters, r => (Count: r.GetInt64(0), P50: NullableMs(r, 1), P95: NullableMs(r, 2)), ct))[0];
            var webhooks = (await QueryAsync(connection, """
                SELECT count(*),
                       count(*) FILTER (WHERE a.response_status BETWEEN 200 AND 299),
                       percentile_cont(0.5) WITHIN GROUP (ORDER BY a.duration_ms)::numeric,
                       percentile_cont(0.95) WITHIN GROUP (ORDER BY a.duration_ms)::numeric
                  FROM webhooks.delivery_attempts a
                  JOIN webhooks.deliveries d ON d.id = a.delivery_id
                 WHERE a.started_at >= @from AND a.started_at < @to
                   AND (@merchant::uuid IS NULL OR d.merchant_id = @merchant)
                """, parameters, r => (Count: r.GetInt64(0), Ok: r.GetInt64(1), P50: NullableMs(r, 2), P95: NullableMs(r, 3)), ct))[0];

            ReconciliationSection? reconciliation = null;
            if (q.IncludeReconciliation)
            {
                var now = clock.GetUtcNow();
                var age = new Dictionary<string, object?> { ["now"] = now };
                var byOutcome = await QueryAsync(connection, """
                    SELECT outcome, count(*) FROM reconciliation.exceptions
                     WHERE status IN ('Open','InReview') GROUP BY outcome ORDER BY count(*) DESC, outcome
                    """, age, r => new OutcomeAge(r.GetString(0), Convert.ToInt32(r.GetInt64(1))), ct);
                var buckets = (await QueryAsync(connection, """
                    SELECT count(*) FILTER (WHERE created_at > @now - interval '1 day'),
                           count(*) FILTER (WHERE created_at <= @now - interval '1 day' AND created_at > @now - interval '7 days'),
                           count(*) FILTER (WHERE created_at <= @now - interval '7 days')
                      FROM reconciliation.exceptions WHERE status IN ('Open','InReview')
                    """, age, r => new[] { r.GetInt64(0), r.GetInt64(1), r.GetInt64(2) }, ct))[0];
                reconciliation = new ReconciliationSection(
                    byOutcome.Sum(o => o.Open),
                    byOutcome,
                    [new AgeBucket("under 1 day", (int)buckets[0]), new AgeBucket("1–7 days", (int)buckets[1]), new AgeBucket("over 7 days", (int)buckets[2])]);
            }

            var succeeded = outcome[0];
            var reversed = outcome[1];
            var finished = outcome[0] + outcome[1] + outcome[2] + outcome[3] + outcome[4];
            var capturedValue = days.Sum(d => d.Amount);

            return new ReportResponse(
                q.MerchantId is { } m ? PublicId.Create(IdPrefixes.Merchant, m) : null,
                q.Currency,
                q.From,
                q.To,
                clock.GetUtcNow(),
                new VolumeSection(
                    days.Sum(d => d.Payments),
                    days.Sum(d => d.Succeeded),
                    Format(capturedValue, currency),
                    days.Select(d => new DailyVolume(d.Day, d.Payments, d.Succeeded, Format(d.Amount, currency))).ToList()),
                new OutcomeSection(
                    (int)succeeded, (int)reversed, (int)outcome[2], (int)outcome[3], (int)outcome[4], (int)outcome[5],
                    Rate(succeeded + reversed, finished), Rate(outcome[2], finished), Rate(outcome[3] + outcome[4], finished),
                    failureCodes),
                new RefundSection((int)refunds.Count, Format(refunds.Amount, currency), (int)refunds.Failed, Rate(refunds.Amount, capturedValue)),
                new SettlementSection(
                    (int)settlements.Batches, (int)settlements.Pending, (int)settlements.Paid, (int)settlements.Failed,
                    Format(settlements.PaidOut, currency), Format(settlements.Awaiting, currency), Format(settlements.Carried, currency)),
                reconciliation,
                new PerformanceSection((int)processing.Count, processing.P50, processing.P95, (int)webhooks.Count, Rate(webhooks.Ok, webhooks.Count), webhooks.P50, webhooks.P95));
        }
        finally
        {
            if (opened)
            {
                await connection.CloseAsync();
            }
        }
    }

    private static async Task<List<T>> QueryAsync<T>(
        NpgsqlConnection connection, string sql, IReadOnlyDictionary<string, object?> parameters, Func<DbDataReader, T> map, CancellationToken ct)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var (name, value) in parameters)
        {
            command.Parameters.Add(value switch
            {
                Guid g => new NpgsqlParameter<Guid>(name, g),
                DateTimeOffset t => new NpgsqlParameter<DateTimeOffset>(name, t),
                string s => new NpgsqlParameter<string>(name, s),
                null => new NpgsqlParameter(name, NpgsqlTypes.NpgsqlDbType.Uuid) { Value = DBNull.Value },
                _ => throw new ArgumentException($"Unsupported parameter {name}."),
            });
        }

        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            rows.Add(map(reader));
        }

        return rows;
    }

    private static long? NullableMs(DbDataReader r, int ordinal) =>
        r.IsDBNull(ordinal) ? null : (long)decimal.Round(r.GetDecimal(ordinal), 0, MidpointRounding.AwayFromZero);

    /// <summary>A fraction rounded to 4 places; 0 when there is nothing to divide by.</summary>
    internal static decimal Rate(decimal part, decimal whole) =>
        whole == 0 ? 0m : decimal.Round(part / whole, 4, MidpointRounding.AwayFromZero);

    private static string Format(decimal amount, Currency currency) => Money.Of(amount, currency).ToAmountString();

    private async Task<ReportResponse?> ReadCacheAsync(string key)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(key);
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<ReportResponse>(value.ToString(), Json);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            logger.LogWarning(ex, "Report cache read failed; computing from the database.");
            return null;
        }
    }

    private async Task WriteCacheAsync(string key, ReportResponse report)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(key, JsonSerializer.Serialize(report, Json), CacheDuration);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Report cache write failed.");
        }
    }
}
