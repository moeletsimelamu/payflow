using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;
using PayFlow.Modules.Merchants.Infrastructure;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Merchants.Application;

/// <summary>API key lifecycle (MER-4). Secrets are returned once and never stored, logged or audited.</summary>
internal sealed class ApiKeyService(
    PayFlowDbContext db,
    ApiKeyCredentials credentials,
    ApiKeyCache cache,
    IAuditLog audit,
    IActorContext actor,
    IOptions<ApiKeySecurityOptions> options,
    IdGenerator ids,
    TimeProvider clock)
{
    private const string Target = "api_key";
    private const string Notice = "Store this secret now. PayFlow keeps only a hash and cannot show it again.";

    public async Task<Result<IReadOnlyList<ApiKeyResponse>>> ListAsync(Guid merchantId, CancellationToken ct)
    {
        if (!await db.Set<Merchant>().AnyAsync(m => m.Id == merchantId, ct))
        {
            return Mapping.MerchantNotFound();
        }

        var now = clock.GetUtcNow();
        var keys = await db.Set<ApiKey>().AsNoTracking().Where(k => k.MerchantId == merchantId).OrderByDescending(k => k.CreatedAt).ToListAsync(ct);
        return keys.Select(k => k.ToResponse(now)).ToList();
    }

    public async Task<Result<CreatedApiKeyResponse>> CreateAsync(Guid merchantId, CreateApiKeyRequest request, CancellationToken ct)
    {
        var check = await EnsureCanIssueAsync(merchantId, ct);
        if (!check.IsSuccess)
        {
            return check.Error;
        }

        var (key, secret) = Issue(merchantId, request.Name);
        audit.Record(new AuditEntry("merchant.api_key.created", Target, PublicId.Create(IdPrefixes.ApiKey, key.Id))
        {
            MerchantId = merchantId,
            Changes = new { key.Name, key.Prefix },
        });
        await db.SaveChangesAsync(ct);
        return new CreatedApiKeyResponse(key.ToResponse(clock.GetUtcNow()), secret, Notice);
    }

    /// <summary>Issues a replacement; the old key keeps working for the grace period, then expires.</summary>
    public async Task<Result<CreatedApiKeyResponse>> RotateAsync(Guid merchantId, Guid keyId, RotateApiKeyRequest request, CancellationToken ct)
    {
        var settings = options.Value;
        var grace = request.GracePeriodHours is { } hours ? TimeSpan.FromHours(hours) : settings.DefaultRotationGracePeriod;
        if (grace < TimeSpan.Zero || grace > settings.MaxRotationGracePeriod)
        {
            return Error.Validation("invalid_grace_period", $"Grace period must be between 0 and {settings.MaxRotationGracePeriod.TotalHours} hours.");
        }

        var old = await db.Set<ApiKey>().SingleOrDefaultAsync(k => k.Id == keyId && k.MerchantId == merchantId, ct);
        if (old is null)
        {
            return KeyNotFound();
        }

        var now = clock.GetUtcNow();
        if (!old.IsUsableAt(now) || old.ReplacedByKeyId is not null)
        {
            return Error.Unprocessable("api_key_not_rotatable", "Only an active key that has not already been rotated can be rotated.");
        }

        var check = await EnsureCanIssueAsync(merchantId, ct);
        if (!check.IsSuccess)
        {
            return check.Error;
        }

        var (replacement, secret) = Issue(merchantId, old.Name);
        old.ReplaceWith(replacement.Id, now + grace);
        audit.Record(new AuditEntry("merchant.api_key.rotated", Target, PublicId.Create(IdPrefixes.ApiKey, old.Id))
        {
            MerchantId = merchantId,
            Changes = new { oldPrefix = old.Prefix, newPrefix = replacement.Prefix, old.ExpiresAt },
        });
        await db.SaveChangesAsync(ct);
        await cache.RemoveAsync([old.Prefix]);
        return new CreatedApiKeyResponse(replacement.ToResponse(now), secret, Notice);
    }

    public async Task<Result<ApiKeyResponse>> RevokeAsync(Guid merchantId, Guid keyId, CancellationToken ct)
    {
        var key = await db.Set<ApiKey>().SingleOrDefaultAsync(k => k.Id == keyId && k.MerchantId == merchantId, ct);
        if (key is null)
        {
            return KeyNotFound();
        }

        var now = clock.GetUtcNow();
        if (key.RevokedAt is null)
        {
            key.Revoke(now);
            audit.Record(new AuditEntry("merchant.api_key.revoked", Target, PublicId.Create(IdPrefixes.ApiKey, key.Id))
            {
                MerchantId = merchantId,
                Changes = new { key.Prefix },
            });
            await db.SaveChangesAsync(ct);
            await cache.RemoveAsync([key.Prefix]);
        }

        return key.ToResponse(now);
    }

    private (ApiKey Key, string Secret) Issue(Guid merchantId, string name)
    {
        var (fullKey, prefix, hash) = credentials.Generate();
        var key = ApiKey.Issue(ids.NewId(), merchantId, name, prefix, hash, actor.Actor.Id ?? actor.Actor.Display, clock.GetUtcNow());
        db.Set<ApiKey>().Add(key);
        return (key, fullKey);
    }

    private async Task<Result> EnsureCanIssueAsync(Guid merchantId, CancellationToken ct)
    {
        var status = await db.Set<Merchant>().Where(m => m.Id == merchantId).Select(m => (MerchantStatus?)m.Status).SingleOrDefaultAsync(ct);
        if (status is null)
        {
            return Mapping.MerchantNotFound();
        }

        if (status == MerchantStatus.Closed)
        {
            return Error.Unprocessable("merchant_closed", "Closed merchants cannot have API keys.");
        }

        var now = clock.GetUtcNow();
        var usable = await db.Set<ApiKey>().CountAsync(
            k => k.MerchantId == merchantId && k.RevokedAt == null && (k.ExpiresAt == null || k.ExpiresAt > now), ct);
        return usable >= options.Value.MaxActiveKeysPerMerchant
            ? Error.Conflict("api_key_limit_reached", $"A merchant can have at most {options.Value.MaxActiveKeysPerMerchant} usable keys. Revoke one first.")
            : Result.Success();
    }

    private static Error KeyNotFound() => Error.NotFound("api_key_not_found", "API key not found.");
}
