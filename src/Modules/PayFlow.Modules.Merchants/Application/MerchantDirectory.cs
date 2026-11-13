using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Merchants.Domain;

namespace PayFlow.Modules.Merchants.Application;

internal sealed class MerchantDirectory(PayFlowDbContext db) : IMerchantDirectory
{
    public async Task<MerchantPaymentProfile?> GetPaymentProfileAsync(Guid merchantId, string currency, CancellationToken cancellationToken)
    {
        var merchant = await db.Set<Merchant>().AsNoTracking()
            .Where(m => m.Id == merchantId)
            .Select(m => new { m.Id, m.Status, m.FeePlanId, Currency = m.Currencies.FirstOrDefault(c => c.Currency == currency) })
            .SingleOrDefaultAsync(cancellationToken);
        if (merchant is null)
        {
            return null;
        }

        if (merchant.Currency is null)
        {
            return new MerchantPaymentProfile(merchant.Id, merchant.Status, null);
        }

        var plan = await db.Set<FeePlan>().AsNoTracking()
            .Where(p => p.Id == merchant.FeePlanId)
            .Select(p => new { p.PercentageRate, Terms = p.Terms.FirstOrDefault(t => t.Currency == currency) })
            .SingleAsync(cancellationToken);
        if (plan.Terms is null)
        {
            return new MerchantPaymentProfile(merchant.Id, merchant.Status, null);
        }

        return new MerchantPaymentProfile(merchant.Id, merchant.Status, new MerchantCurrencyTerms(
            currency,
            merchant.Currency.MaxPaymentAmount,
            merchant.Currency.OperatorRefundLimit,
            merchant.FeePlanId,
            plan.PercentageRate,
            plan.Terms.FixedFee,
            plan.Terms.MinPaymentAmount));
    }
}
