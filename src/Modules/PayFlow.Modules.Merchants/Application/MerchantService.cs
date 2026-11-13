using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;
using PayFlow.Modules.Merchants.Infrastructure;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Merchants.Application;

/// <summary>Merchant onboarding, profile, status, currencies and fee plans (MER-1, MER-2, MER-3, MER-6).</summary>
internal sealed class MerchantService(
    PayFlowDbContext db,
    IAuditLog audit,
    IMerchantMembershipAdmin memberships,
    ApiKeyCache keyCache,
    IdGenerator ids,
    TimeProvider clock)
{
    private const string Target = "merchant";

    public async Task<Result<RegisterMerchantResponse>> RegisterAsync(RegisterMerchantRequest request, CancellationToken ct)
    {
        var feePlanId = FeePlan.StandardV1Id;
        if (request.FeePlanId is not null)
        {
            if (!PublicId.TryParse(request.FeePlanId, IdPrefixes.FeePlan, out var parsed))
            {
                return Error.Validation("invalid_fee_plan_id", "feePlanId must be an fp_... id.");
            }

            feePlanId = parsed.Value;
        }

        var feePlan = await LoadFeePlanAsync(feePlanId, ct);
        if (feePlan is null)
        {
            return Error.Unprocessable("fee_plan_not_found", "The fee plan does not exist.");
        }

        var now = clock.GetUtcNow();
        var merchant = Merchant.Register(ids.NewId(), request.LegalName, request.TradingName, request.ContactEmail, request.CountryCode, feePlan.Id, now);
        foreach (var c in request.Currencies)
        {
            var configured = ConfigureCurrency(merchant, c.Currency, c.MaxPaymentAmount, c.OperatorRefundLimit, feePlan, now);
            if (!configured.IsSuccess)
            {
                return configured.Error;
            }
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        db.Set<Merchant>().Add(merchant);
        var merchantPublicId = PublicId.Create(IdPrefixes.Merchant, merchant.Id);
        audit.Record(new AuditEntry("merchant.registered", Target, merchantPublicId)
        {
            MerchantId = merchant.Id,
            Changes = new { merchant.LegalName, merchant.TradingName, merchant.CountryCode, currencies = merchant.Currencies.Select(x => x.Currency) },
        });

        MemberInvitationResponse? invitation = null;
        if (!string.IsNullOrWhiteSpace(request.AdminEmail))
        {
            // Merchant row first, so the membership's foreign key is satisfied.
            await db.SaveChangesAsync(ct);
            var invited = await memberships.InviteAsync(
                merchant.Id, request.AdminEmail.Trim(), request.AdminDisplayName ?? request.AdminEmail.Trim(), MerchantRoles.MerchantAdmin, ct);
            if (!invited.IsSuccess)
            {
                return invited.Error;
            }

            invitation = ToResponse(invited.Value);
            audit.Record(new AuditEntry("merchant.member.invited", "user", invitation.UserId)
            {
                MerchantId = merchant.Id,
                Changes = new { email = invitation.Email, role = invitation.Role },
            });
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new RegisterMerchantResponse(merchant.ToResponse(), invitation);
    }

    public async Task<PagedResponse<MerchantResponse>> ListAsync(MerchantStatus? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Set<Merchant>().AsNoTracking().Include(m => m.Currencies).AsQueryable();
        if (status is { } s)
        {
            query = query.Where(m => m.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(search))
        {
            var pattern = $"%{search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            query = query.Where(m => EF.Functions.ILike(m.LegalName, pattern) || EF.Functions.ILike(m.TradingName, pattern));
        }

        var total = await query.CountAsync(ct);
        var items = await query.OrderByDescending(m => m.CreatedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResponse<MerchantResponse>(items.Select(m => m.ToResponse()).ToList(), page, pageSize, total);
    }

    public async Task<Result<MerchantResponse>> GetAsync(Guid merchantId, CancellationToken ct)
    {
        var merchant = await db.Set<Merchant>().AsNoTracking().Include(m => m.Currencies).SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        return merchant is null ? Mapping.MerchantNotFound() : merchant.ToResponse();
    }

    public async Task<Result<MerchantApiProfileResponse>> GetApiProfileAsync(Guid merchantId, CancellationToken ct)
    {
        var merchant = await db.Set<Merchant>().AsNoTracking().Include(m => m.Currencies).SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        return merchant is null
            ? Mapping.MerchantNotFound()
            : new MerchantApiProfileResponse(
                PublicId.Create(IdPrefixes.Merchant, merchant.Id),
                merchant.TradingName,
                merchant.Status,
                merchant.Currencies.OrderBy(c => c.Currency).Select(c => c.ToResponse()).ToList(),
                Simulated: true);
    }

    public Task<Result<MerchantResponse>> UpdateProfileAsync(Guid merchantId, UpdateMerchantProfileRequest request, CancellationToken ct) =>
        MutateAsync(merchantId, "merchant.profile_updated", (m, now) =>
        {
            var before = new { m.TradingName, m.ContactEmail };
            var result = m.UpdateProfile(request.TradingName, request.ContactEmail, now);
            return (result, new { before, after = new { m.TradingName, m.ContactEmail } });
        }, ct);

    public async Task<Result<MerchantResponse>> ChangeStatusAsync(Guid merchantId, ChangeMerchantStatusRequest request, CancellationToken ct)
    {
        var result = await MutateAsync(merchantId, "merchant.status_changed", (m, now) =>
        {
            var from = m.Status;
            var changed = m.ChangeStatus(request.Status, request.Reason, now);
            return (changed, new { from, to = m.Status, reason = request.Reason });
        }, ct);

        if (result.IsSuccess)
        {
            // Cached key lookups carry the merchant status; drop them so a closed merchant's keys stop working now.
            var prefixes = await db.Set<ApiKey>().Where(k => k.MerchantId == merchantId).Select(k => k.Prefix).ToListAsync(ct);
            await keyCache.RemoveAsync(prefixes);
        }

        return result;
    }

    public async Task<Result<MerchantResponse>> ConfigureCurrencyAsync(Guid merchantId, string currencyCode, CurrencyLimitsRequest request, CancellationToken ct)
    {
        var merchant = await db.Set<Merchant>().Include(m => m.Currencies).SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null)
        {
            return Mapping.MerchantNotFound();
        }

        var feePlan = await LoadFeePlanAsync(merchant.FeePlanId, ct);
        var result = ConfigureCurrency(merchant, currencyCode, request.MaxPaymentAmount, request.OperatorRefundLimit, feePlan!, clock.GetUtcNow());
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry("merchant.currency_configured", Target, PublicId.Create(IdPrefixes.Merchant, merchantId))
        {
            MerchantId = merchantId,
            Changes = new { currency = currencyCode, request.MaxPaymentAmount, request.OperatorRefundLimit },
        });
        await db.SaveChangesAsync(ct);
        return merchant.ToResponse();
    }

    public async Task<Result<MerchantResponse>> ChangeFeePlanAsync(Guid merchantId, ChangeFeePlanRequest request, CancellationToken ct)
    {
        if (!PublicId.TryParse(request.FeePlanId, IdPrefixes.FeePlan, out var feePlanId))
        {
            return Error.Validation("invalid_fee_plan_id", "feePlanId must be an fp_... id.");
        }

        var feePlan = await LoadFeePlanAsync(feePlanId.Value, ct);
        if (feePlan is null)
        {
            return Error.Unprocessable("fee_plan_not_found", "The fee plan does not exist.");
        }

        return await MutateAsync(merchantId, "merchant.fee_plan_changed", (m, now) =>
        {
            var from = PublicId.Create(IdPrefixes.FeePlan, m.FeePlanId);
            var changed = m.ChangeFeePlan(feePlan, now);
            return (changed, new { from, to = request.FeePlanId });
        }, ct);
    }

    public async Task<IReadOnlyList<FeePlanResponse>> ListFeePlansAsync(CancellationToken ct) =>
        (await db.Set<FeePlan>().AsNoTracking().Include(p => p.Terms).OrderBy(p => p.Name).ThenByDescending(p => p.Version).ToListAsync(ct))
            .Select(p => p.ToResponse())
            .ToList();

    public async Task<Result<FeePlanResponse>> CreateFeePlanAsync(CreateFeePlanRequest request, CancellationToken ct)
    {
        if (!decimal.TryParse(request.PercentageRate, System.Globalization.NumberStyles.AllowDecimalPoint, System.Globalization.CultureInfo.InvariantCulture, out var rate))
        {
            return Error.Validation("invalid_rate", "percentageRate must be a decimal string such as \"0.029\".");
        }

        var terms = new List<(Currency, decimal, decimal)>();
        foreach (var t in request.Terms)
        {
            if (!Currency.TryFromCode(t.Currency, out var currency))
            {
                return Mapping.UnsupportedCurrency(t.Currency);
            }

            var fixedFee = Mapping.ParseAmount(t.FixedFee, t.Currency, "fixedFee");
            var minimum = Mapping.ParseAmount(t.MinPaymentAmount, t.Currency, "minPaymentAmount");
            if (!fixedFee.IsSuccess)
            {
                return fixedFee.Error;
            }

            if (!minimum.IsSuccess)
            {
                return minimum.Error;
            }

            terms.Add((currency, fixedFee.Value!.Value, minimum.Value!.Value));
        }

        var name = request.Name.Trim();
        var latest = await db.Set<FeePlan>().Where(p => p.Name == name).MaxAsync(p => (int?)p.Version, ct) ?? 0;
        var created = FeePlan.Create(ids.NewId(), name, latest + 1, rate, terms, clock.GetUtcNow());
        if (!created.IsSuccess)
        {
            return created.Error;
        }

        db.Set<FeePlan>().Add(created.Value);
        audit.Record(new AuditEntry("platform.fee_plan.created", "fee_plan", PublicId.Create(IdPrefixes.FeePlan, created.Value.Id))
        {
            Changes = new { name, version = latest + 1, rate = request.PercentageRate, request.Terms },
        });
        await db.SaveChangesAsync(ct);
        return created.Value.ToResponse();
    }

    internal static MemberInvitationResponse ToResponse(MemberInvitation invitation) => new(
        PublicId.Create(IdPrefixes.User, invitation.UserId), invitation.Email, invitation.Role, invitation.InvitationToken);

    private Task<FeePlan?> LoadFeePlanAsync(Guid id, CancellationToken ct) =>
        db.Set<FeePlan>().Include(p => p.Terms).SingleOrDefaultAsync(p => p.Id == id, ct);

    private static Result ConfigureCurrency(Merchant merchant, string currencyCode, string max, string? operatorLimit, FeePlan feePlan, DateTimeOffset now)
    {
        if (!Currency.TryFromCode(currencyCode, out var currency))
        {
            return Mapping.UnsupportedCurrency(currencyCode);
        }

        var maxAmount = Mapping.ParseAmount(max, currencyCode, "maxPaymentAmount");
        if (!maxAmount.IsSuccess)
        {
            return maxAmount.Error;
        }

        var limit = Mapping.ParseAmount(operatorLimit, currencyCode, "operatorRefundLimit");
        return limit.IsSuccess
            ? merchant.ConfigureCurrency(currency, maxAmount.Value!.Value, limit.Value, feePlan, now)
            : limit.Error;
    }

    private async Task<Result<MerchantResponse>> MutateAsync(
        Guid merchantId,
        string action,
        Func<Merchant, DateTimeOffset, (Result Result, object Changes)> change,
        CancellationToken ct)
    {
        var merchant = await db.Set<Merchant>().Include(m => m.Currencies).SingleOrDefaultAsync(m => m.Id == merchantId, ct);
        if (merchant is null)
        {
            return Mapping.MerchantNotFound();
        }

        var (result, changes) = change(merchant, clock.GetUtcNow());
        if (!result.IsSuccess)
        {
            return result.Error;
        }

        audit.Record(new AuditEntry(action, Target, PublicId.Create(IdPrefixes.Merchant, merchantId)) { MerchantId = merchantId, Changes = changes });
        await db.SaveChangesAsync(ct); // xmin concurrency: a concurrent edit raises DbUpdateConcurrencyException → 409
        return merchant.ToResponse();
    }
}
