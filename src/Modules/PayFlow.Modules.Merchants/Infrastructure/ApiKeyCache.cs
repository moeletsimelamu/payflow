using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Modules.Merchants.Application;
using PayFlow.Modules.Merchants.Contracts;
using StackExchange.Redis;

namespace PayFlow.Modules.Merchants.Infrastructure;

/// <summary>What the authenticator needs about a key, cached per prefix. Holds the HMAC, never the secret.</summary>
internal sealed record CachedApiKey(
    Guid KeyId,
    Guid MerchantId,
    string Prefix,
    byte[] SecretHash,
    DateTimeOffset? ExpiresAt,
    DateTimeOffset? RevokedAt,
    MerchantStatus MerchantStatus);

/// <summary>
/// Short-lived Redis cache of API key lookups. Entries are deleted whenever a key or its merchant
/// changes, and expire after <see cref="ApiKeySecurityOptions.CacheDuration"/> regardless. Redis is
/// an optimisation only: if it is unavailable, lookups fall through to PostgreSQL.
/// </summary>
internal sealed class ApiKeyCache(IConnectionMultiplexer redis, IOptions<ApiKeySecurityOptions> options, ILogger<ApiKeyCache> logger)
{
    private static string Key(string prefix) => $"payflow:apikey:{prefix}";

    public async Task<CachedApiKey?> GetAsync(string prefix)
    {
        try
        {
            var value = await redis.GetDatabase().StringGetAsync(Key(prefix));
            return value.IsNullOrEmpty ? null : JsonSerializer.Deserialize<CachedApiKey>(value.ToString());
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException or JsonException)
        {
            logger.LogWarning(ex, "API key cache read failed for prefix {KeyPrefix}; using the database.", prefix);
            return null;
        }
    }

    public async Task SetAsync(CachedApiKey key)
    {
        try
        {
            await redis.GetDatabase().StringSetAsync(Key(key.Prefix), JsonSerializer.Serialize(key), options.Value.CacheDuration);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            logger.LogWarning(ex, "API key cache write failed for prefix {KeyPrefix}.", key.Prefix);
        }
    }

    public async Task RemoveAsync(IEnumerable<string> prefixes)
    {
        var keys = prefixes.Select(p => (RedisKey)Key(p)).ToArray();
        if (keys.Length == 0)
        {
            return;
        }

        try
        {
            await redis.GetDatabase().KeyDeleteAsync(keys);
        }
        catch (Exception ex) when (ex is RedisException or TimeoutException)
        {
            // Stale entries expire within CacheDuration; revocation is therefore effective within that bound even if Redis is down.
            logger.LogError(ex, "API key cache invalidation failed; entries expire within {CacheDuration}.", options.Value.CacheDuration);
        }
    }
}
