using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace PayFlow.Infrastructure.RateLimiting;

public sealed record RateLimitDecision(bool Allowed, int Limit, long Remaining, TimeSpan RetryAfter);

/// <summary>
/// Fixed-window counter shared by all API instances through Redis. The increment and expiry run as
/// one Lua script, so concurrent requests are counted exactly.
/// <para>
/// Fails <b>open</b> when Redis is unavailable: rate limiting protects capacity, not money, and an
/// outage of the cache must not take the payment API down. Financial safety does not depend on it
/// (idempotency and constraints live in PostgreSQL).
/// </para>
/// </summary>
public sealed class RedisRateLimiter(IConnectionMultiplexer redis, TimeProvider clock, ILogger<RedisRateLimiter> logger)
{
    private const string Script = """
        local count = redis.call('INCR', KEYS[1])
        if count == 1 then redis.call('PEXPIRE', KEYS[1], ARGV[1]) end
        return count
        """;

    public async Task<RateLimitDecision> AcquireAsync(string partition, int limit, TimeSpan window)
    {
        ArgumentException.ThrowIfNullOrEmpty(partition);
        var windowMs = (long)window.TotalMilliseconds;
        var now = clock.GetUtcNow();
        var index = now.ToUnixTimeMilliseconds() / windowMs;
        var retryAfter = DateTimeOffset.FromUnixTimeMilliseconds((index + 1) * windowMs) - now;

        try
        {
            var result = await redis.GetDatabase().ScriptEvaluateAsync(
                Script,
                [(RedisKey)$"payflow:rl:{partition}:{index}"],
                [windowMs + 1000]);
            var count = (long)result;
            return new RateLimitDecision(count <= limit, limit, Math.Max(0, limit - count), retryAfter);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "Rate limiter unavailable; allowing request for partition {Partition}.", partition);
            return new RateLimitDecision(true, limit, limit, TimeSpan.Zero);
        }
    }
}
