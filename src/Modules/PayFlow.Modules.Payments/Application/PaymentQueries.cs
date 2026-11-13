using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Payments.Application;

/// <summary>Read side for the merchant API and the dashboard. Every query is scoped by merchant id.</summary>
internal sealed class PaymentQueries(PayFlowDbContext db, ILedger ledger)
{
    public async Task<Result<PaymentResponse>> GetAsync(Guid merchantId, Guid paymentId, CancellationToken ct)
    {
        var payment = await LoadAsync(merchantId, paymentId, ct);
        return payment is null ? NotFound() : payment.ToResponse(includeHistory: true);
    }

    /// <summary>Cursor pagination, newest first. UUIDv7 ids are time-ordered, so the id is the cursor.</summary>
    public async Task<Result<PaymentListResponse>> ListAsync(Guid merchantId, PaymentStatus? status, int limit, string? startingAfter, CancellationToken ct)
    {
        limit = Math.Clamp(limit, 1, 100);
        var query = db.Set<Payment>().AsNoTracking().Where(p => p.MerchantId == merchantId);
        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        if (startingAfter is not null)
        {
            if (!PublicId.TryParse(startingAfter, IdPrefixes.Payment, out var cursor))
            {
                return Error.Validation("invalid_cursor", "startingAfter must be a pay_... id.");
            }

            query = query.Where(p => p.Id.CompareTo(cursor.Value) < 0);
        }

        var page = await query.OrderByDescending(p => p.Id).Take(limit + 1).ToListAsync(ct);
        var hasMore = page.Count > limit;
        var items = page.Take(limit).Select(p => p.ToResponse(includeHistory: false)).ToList();
        return new PaymentListResponse(items, hasMore, hasMore ? items[^1].Id : null);
    }

    public async Task<Result<PaymentDetailResponse>> GetDetailAsync(Guid merchantId, Guid paymentId, CancellationToken ct)
    {
        var payment = await LoadAsync(merchantId, paymentId, ct);
        if (payment is null)
        {
            return NotFound();
        }

        var entries = await ledger.GetEntriesForSourceAsync(PaymentProcessor.ResourceType, payment.Id, ct);
        return new PaymentDetailResponse(
            payment.ToResponse(includeHistory: true),
            PublicId.Create(IdPrefixes.Merchant, payment.MerchantId),
            payment.IdempotencyKey,
            PublicId.Create(IdPrefixes.FeePlan, payment.FeePlanId),
            PaymentService.FormatRate(payment.FeeRate),
            payment.ProcessingStartedAt,
            payment.CompletedAt - payment.ProcessingStartedAt is { } elapsed ? (long)elapsed.TotalMilliseconds : null,
            entries.Select(e => new LedgerEntryResponse(
                    e.EntryType,
                    e.Description,
                    e.OccurredAt,
                    e.Postings.Select(p => new LedgerPostingResponse(p.Account, p.Direction, Money.Of(p.Amount, e.Currency).ToAmountString())).ToList()))
                .ToList());
    }

    /// <summary>Dashboard search. <paramref name="merchantId"/> null means all merchants (PlatformAdmin only).</summary>
    public async Task<DashboardPaymentPage> SearchAsync(
        Guid? merchantId, PaymentStatus? status, string? failureCode, string? reference, DateTimeOffset? from, DateTimeOffset? to, int page, int pageSize, CancellationToken ct)
    {
        page = Math.Max(page, 1);
        pageSize = Math.Clamp(pageSize, 1, 100);
        var query = db.Set<Payment>().AsNoTracking();
        if (merchantId is { } m)
        {
            query = query.Where(p => p.MerchantId == m);
        }

        if (status is { } s)
        {
            query = query.Where(p => p.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(failureCode))
        {
            query = query.Where(p => p.FailureCode == failureCode);
        }

        if (!string.IsNullOrWhiteSpace(reference))
        {
            query = query.Where(p => p.MerchantReference == reference.Trim());
        }

        if (from is { } f)
        {
            query = query.Where(p => p.CreatedAt >= f);
        }

        if (to is { } t)
        {
            query = query.Where(p => p.CreatedAt < t);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(p => p.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new DashboardPaymentPage(
            rows.Select(p => new DashboardPaymentRow(
                    PublicId.Create(IdPrefixes.Payment, p.Id),
                    PublicId.Create(IdPrefixes.Merchant, p.MerchantId),
                    p.MerchantReference,
                    p.AmountMoney.ToAmountString(),
                    p.Currency,
                    p.Status,
                    p.FeeMoney?.ToAmountString(),
                    p.FailureCode,
                    p.CreatedAt))
                .ToList(),
            page,
            pageSize,
            total);
    }

    private Task<Payment?> LoadAsync(Guid merchantId, Guid paymentId, CancellationToken ct) =>
        db.Set<Payment>().AsNoTracking().Include(p => p.History).SingleOrDefaultAsync(p => p.Id == paymentId && p.MerchantId == merchantId, ct);

    private static Error NotFound() => Error.NotFound("payment_not_found", "Payment not found.");
}
