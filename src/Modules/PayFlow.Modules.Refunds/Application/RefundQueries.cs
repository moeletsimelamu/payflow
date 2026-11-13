using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Refunds.Application;

internal sealed class RefundQueries(PayFlowDbContext db, IPaymentRefundOperations payments)
{
    public async Task<Result<RefundResponse>> GetAsync(Guid merchantId, Guid refundId, CancellationToken ct)
    {
        var refund = await db.Set<Refund>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == refundId && r.MerchantId == merchantId, ct);
        return refund is null ? Error.NotFound("refund_not_found", "Refund not found.") : refund.ToResponse();
    }

    /// <summary>The full refund history of a payment (REF-3), plus its reversal if any.</summary>
    public async Task<Result<PaymentRefundsResponse>> ForPaymentAsync(Guid merchantId, Guid paymentId, CancellationToken ct)
    {
        if (await payments.FindAsync(merchantId, paymentId, ct) is null)
        {
            return Error.NotFound("payment_not_found", "Payment not found.");
        }

        var refunds = await db.Set<Refund>().AsNoTracking()
            .Where(r => r.PaymentId == paymentId && r.MerchantId == merchantId)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync(ct);
        var reversal = await db.Set<Reversal>().AsNoTracking().SingleOrDefaultAsync(r => r.PaymentId == paymentId && r.MerchantId == merchantId, ct);
        return new PaymentRefundsResponse(
            PublicId.Create(IdPrefixes.Payment, paymentId),
            refunds.Select(r => r.ToResponse()).ToList(),
            reversal?.ToResponse());
    }
}
