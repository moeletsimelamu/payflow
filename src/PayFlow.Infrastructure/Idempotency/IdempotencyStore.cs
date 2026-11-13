using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Infrastructure.Idempotency;

public sealed class IdempotencyOptions
{
    public const string Section = "Idempotency";

    /// <summary>How long a key and its stored response are kept (docs/PAYMENT_LIFECYCLE.md §7, rule 6).</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromHours(24);
}

/// <summary>Row of <c>platform.idempotency_records</c>.</summary>
public sealed class IdempotencyRecord
{
    public const string InProgress = "InProgress";
    public const string Completed = "Completed";

    public required string Scope { get; init; }

    public required string Operation { get; init; }

    public required string Key { get; init; }

    public required byte[] RequestHash { get; init; }

    public required string Status { get; init; }

    public int? ResponseStatus { get; init; }

    /// <summary>The exact response body, stored as text so a replay is byte-for-byte identical.</summary>
    public string? ResponseBody { get; init; }

    public string? ResourceType { get; init; }

    public Guid? ResourceId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset ExpiresAt { get; init; }
}

public enum IdempotencyClaimKind
{
    /// <summary>This request owns the key and must complete or release it.</summary>
    Claimed,

    /// <summary>The same request already completed: replay its response.</summary>
    Replay,

    /// <summary>The same request is still being processed elsewhere.</summary>
    InProgress,

    /// <summary>The key was used for a different request.</summary>
    Mismatch,
}

public sealed record IdempotencyClaim(IdempotencyClaimKind Kind, int? ResponseStatus = null, string? ResponseBody = null);

public sealed record IdempotencyKeyRef(string Scope, string Operation, string Key);

/// <summary>
/// PostgreSQL-backed idempotency (ADR-004, docs/PAYMENT_LIFECYCLE.md §7).
/// <para>
/// <see cref="ClaimAsync"/> commits the claim <b>before</b> any financial work, outside the business
/// transaction. The primary key gives exactly one winner among concurrent requests; the others
/// block on the insert until it commits, then see the record.
/// </para>
/// </summary>
public sealed class IdempotencyStore(PayFlowDbContext db, TimeProvider clock, IOptions<IdempotencyOptions> options)
{
    public async Task<IdempotencyClaim> ClaimAsync(IdempotencyKeyRef key, byte[] requestHash, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(requestHash);
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var now = clock.GetUtcNow();
            var expires = now + options.Value.Retention;

            // An expired record no longer protects anything here; the permanent unique constraint on the
            // resource table still prevents a duplicate financial operation.
            await db.Database.ExecuteSqlAsync(
                $"DELETE FROM platform.idempotency_records WHERE scope = {key.Scope} AND operation = {key.Operation} AND key = {key.Key} AND expires_at <= {now}",
                ct);

            var inserted = await db.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO platform.idempotency_records (scope, operation, key, request_hash, status, created_at, expires_at)
                VALUES ({key.Scope}, {key.Operation}, {key.Key}, {requestHash}, {IdempotencyRecord.InProgress}, {now}, {expires})
                ON CONFLICT DO NOTHING
                """,
                ct);
            if (inserted == 1)
            {
                return new IdempotencyClaim(IdempotencyClaimKind.Claimed);
            }

            var existing = await db.Set<IdempotencyRecord>().AsNoTracking()
                .SingleOrDefaultAsync(r => r.Scope == key.Scope && r.Operation == key.Operation && r.Key == key.Key, ct);
            if (existing is null)
            {
                continue; // released between our insert and read: try again
            }

            if (!CryptographicOperations.FixedTimeEquals(existing.RequestHash, requestHash))
            {
                return new IdempotencyClaim(IdempotencyClaimKind.Mismatch);
            }

            return existing.Status == IdempotencyRecord.Completed
                ? new IdempotencyClaim(IdempotencyClaimKind.Replay, existing.ResponseStatus, existing.ResponseBody)
                : new IdempotencyClaim(IdempotencyClaimKind.InProgress);
        }

        return new IdempotencyClaim(IdempotencyClaimKind.InProgress);
    }

    /// <summary>Links the claim to the resource it created, so a recovery worker can complete it later.</summary>
    public Task AttachResourceAsync(IdempotencyKeyRef key, string resourceType, Guid resourceId, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"UPDATE platform.idempotency_records SET resource_type = {resourceType}, resource_id = {resourceId} WHERE scope = {key.Scope} AND operation = {key.Operation} AND key = {key.Key}",
            ct);

    /// <summary>Stores the final response. Call inside the business transaction that produced it.</summary>
    public Task CompleteForResourceAsync(string resourceType, Guid resourceId, int responseStatus, string responseBody, CancellationToken ct) =>
        db.Database.ExecuteSqlAsync(
            $"""
            UPDATE platform.idempotency_records
               SET status = {IdempotencyRecord.Completed}, response_status = {responseStatus}, response_body = {responseBody}
             WHERE resource_type = {resourceType} AND resource_id = {resourceId} AND status = {IdempotencyRecord.InProgress}
            """,
            ct);

    /// <summary>Gives the key back when nothing was executed (validation or business rejection, unexpected error).</summary>
    public Task ReleaseAsync(IdempotencyKeyRef key) =>
        db.Database.ExecuteSqlAsync(
            $"DELETE FROM platform.idempotency_records WHERE scope = {key.Scope} AND operation = {key.Operation} AND key = {key.Key} AND status = {IdempotencyRecord.InProgress} AND resource_id IS NULL",
            CancellationToken.None);

    public Task<int> SweepExpiredAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        return db.Database.ExecuteSqlAsync($"DELETE FROM platform.idempotency_records WHERE expires_at <= {now}", ct);
    }
}

public static class IdempotencyKeys
{
    public const string HeaderName = "Idempotency-Key";

    /// <summary>1–255 printable ASCII characters, no spaces.</summary>
    public static Result<string> Validate(string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return Error.Validation("idempotency_key_required", $"The {HeaderName} header is required for this operation.");
        }

        return header.Length <= 255 && header.All(c => c is >= '!' and <= '~')
            ? header
            : Error.Validation("idempotency_key_invalid", $"{HeaderName} must be 1–255 printable ASCII characters without spaces.");
    }

    /// <summary>
    /// SHA-256 over method, route and the canonical JSON of the request (object keys sorted, no
    /// whitespace), so semantically identical retries match regardless of formatting or key order.
    /// </summary>
    public static byte[] HashRequest(string method, string route, object? body, JsonSerializerOptions serializerOptions)
    {
        var node = body is null ? null : JsonSerializer.SerializeToNode(body, body.GetType(), serializerOptions);
        var canonical = Canonicalize(node)?.ToJsonString() ?? "null";
        return SHA256.HashData(Encoding.UTF8.GetBytes($"{method.ToUpperInvariant()}\n{route}\n{canonical}"));
    }

    private static JsonNode? Canonicalize(JsonNode? node) => node switch
    {
        JsonObject obj => new JsonObject(obj
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Canonicalize(p.Value)))),
        JsonArray array => new JsonArray(array.Select(Canonicalize).ToArray()),
        null => null,
        _ => node.DeepClone(),
    };
}
