using System.Collections.Concurrent;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PayFlow.Infrastructure.Http;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;
using PayFlow.Modules.Merchants.Infrastructure;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Merchants.Application;

internal sealed record AuthenticatedApiKey(Guid KeyId, Guid MerchantId, string Prefix);

/// <summary>Throttles <c>last_used_at</c> writes to one per key per minute per instance.</summary>
internal sealed class ApiKeyUsageTracker
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);
    private readonly ConcurrentDictionary<Guid, DateTimeOffset> _lastTouched = new();

    public bool ShouldTouch(Guid keyId, DateTimeOffset now)
    {
        var last = _lastTouched.GetOrAdd(keyId, DateTimeOffset.MinValue);
        return now - last >= Interval && _lastTouched.TryUpdate(keyId, now, last);
    }
}

/// <summary>Validates a presented API key: format, lookup by prefix, constant-time HMAC check, state and merchant status.</summary>
internal sealed class ApiKeyAuthenticator(
    PayFlowDbContext db,
    ApiKeyCredentials credentials,
    ApiKeyCache cache,
    ApiKeyUsageTracker usage,
    TimeProvider clock)
{
    public async Task<AuthenticatedApiKey?> AuthenticateAsync(string token, CancellationToken cancellationToken)
    {
        if (!ApiKeyCredentials.TryParsePrefix(token, out var prefix))
        {
            return null;
        }

        var key = await cache.GetAsync(prefix);
        if (key is null)
        {
            key = await (from k in db.Set<ApiKey>().AsNoTracking()
                         join m in db.Set<Merchant>().AsNoTracking() on k.MerchantId equals m.Id
                         where k.Prefix == prefix
                         select new CachedApiKey(k.Id, k.MerchantId, k.Prefix, k.SecretHash, k.ExpiresAt, k.RevokedAt, m.Status))
                .SingleOrDefaultAsync(cancellationToken);
            if (key is null)
            {
                return null;
            }

            await cache.SetAsync(key);
        }

        if (!credentials.Verify(token, key.SecretHash))
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (key.RevokedAt is not null || key.ExpiresAt <= now || key.MerchantStatus == MerchantStatus.Closed)
        {
            return null;
        }

        if (usage.ShouldTouch(key.KeyId, now))
        {
            await db.Set<ApiKey>().Where(k => k.Id == key.KeyId)
                .ExecuteUpdateAsync(s => s.SetProperty(k => k.LastUsedAt, now), cancellationToken);
        }

        return new AuthenticatedApiKey(key.KeyId, key.MerchantId, key.Prefix);
    }
}

/// <summary><c>Authorization: Bearer pf_test_sk_...</c> authentication for the merchant API.</summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    ApiKeyAuthenticator authenticator)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    private const string BearerPrefix = "Bearer ";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var header = Request.Headers.Authorization.ToString();
        if (string.IsNullOrEmpty(header))
        {
            return AuthenticateResult.NoResult();
        }

        if (!header.StartsWith(BearerPrefix, StringComparison.Ordinal))
        {
            return AuthenticateResult.Fail("Unsupported authorization scheme.");
        }

        var key = await authenticator.AuthenticateAsync(header[BearerPrefix.Length..].Trim(), Context.RequestAborted);
        if (key is null)
        {
            // The key itself is never logged; the prefix is safe to log only after it has been parsed.
            Logger.LogWarning("Rejected merchant API request with an invalid, expired or revoked API key.");
            return AuthenticateResult.Fail("Invalid API key.");
        }

        var publicId = PublicId.Create(IdPrefixes.ApiKey, key.KeyId);
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.NameIdentifier, key.KeyId.ToString("D")),
                new Claim(ClaimTypes.Name, $"api_key:{key.Prefix}"),
                new Claim(PayFlowClaimTypes.ActorType, nameof(ActorType.ApiKey)),
                new Claim(PayFlowClaimTypes.PublicId, publicId),
                new Claim(PayFlowClaimTypes.MerchantId, key.MerchantId.ToString("D")),
            ],
            Scheme.Name);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), Scheme.Name));
    }

    protected override async Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.Headers.WWWAuthenticate = "Bearer realm=\"PayFlow\"";
        var error = Request.Headers.Authorization.Count > 0
            ? Error.Unauthorized("invalid_api_key", "The API key is invalid, expired or revoked.")
            : Error.Unauthorized("api_key_required", "Send your API key as 'Authorization: Bearer pf_test_sk_...'.");
        await ErrorResults.Problem(error).ExecuteAsync(Context);
    }

    protected override Task HandleForbiddenAsync(AuthenticationProperties properties) =>
        ErrorResults.Problem(Error.Forbidden("forbidden", "This API key may not perform this action.")).ExecuteAsync(Context);
}

internal sealed class CurrentMerchant(IHttpContextAccessor accessor) : ICurrentMerchant
{
    private ClaimsPrincipal? Principal => accessor.HttpContext?.User;

    private bool IsApiKey => Principal?.FindFirstValue(PayFlowClaimTypes.ActorType) == nameof(ActorType.ApiKey);

    public Guid? MerchantId =>
        IsApiKey && Guid.TryParse(Principal!.FindFirstValue(PayFlowClaimTypes.MerchantId), out var id) ? id : null;

    public Guid? ApiKeyId =>
        IsApiKey && Guid.TryParse(Principal!.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;
}
