using Microsoft.EntityFrameworkCore;
using PayFlow.Infrastructure.Correlation;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Payments.Domain;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Payments.Application;

internal sealed class PaymentRefundOperations(PayFlowDbContext db, ICorrelationContext correlation, TimeProvider clock) : IPaymentRefundOperations
{
    public async Task<PaymentSnapshot?> FindAsync(Guid merchantId, Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await db.Set<Payment>().AsNoTracking().SingleOrDefaultAsync(p => p.Id == paymentId && p.MerchantId == merchantId, cancellationToken);
        return payment is null ? null : Snapshot(payment);
    }

    public async Task<PaymentSnapshot?> LockAsync(Guid merchantId, Guid paymentId, CancellationToken cancellationToken)
    {
        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Locking a payment requires an open transaction; the lock is held until it commits.");
        }

        var locked = await db.Database
            .SqlQuery<Guid>($"SELECT id AS \"Value\" FROM payments.payments WHERE id = {paymentId} AND merchant_id = {merchantId} FOR UPDATE")
            .ToListAsync(cancellationToken);
        if (locked.Count == 0)
        {
            return null;
        }

        // Under the lock, a fresh read sees the latest committed state.
        var payment = await LoadTrackedAsync(paymentId, reload: true, cancellationToken);
        return Snapshot(payment);
    }

    public async Task<Result<PaymentSnapshot>> ReserveRefundAsync(Guid paymentId, decimal amount, CancellationToken cancellationToken)
    {
        var payment = await LoadTrackedAsync(paymentId, reload: false, cancellationToken);
        var reserved = payment.ReserveRefund(amount, clock.GetUtcNow());
        return reserved.IsSuccess ? Snapshot(payment) : reserved.Error;
    }

    public async Task<PaymentSnapshot> CompleteRefundAsync(Guid paymentId, decimal amount, bool succeeded, Actor actor, CancellationToken cancellationToken)
    {
        var payment = await LoadTrackedAsync(paymentId, reload: false, cancellationToken);
        payment.CompleteRefund(amount, succeeded, clock.GetUtcNow(), actor, correlation.CorrelationId);
        return Snapshot(payment);
    }

    public async Task<Result<PaymentSnapshot>> ReverseAsync(Guid paymentId, bool isSettled, string reason, Actor actor, CancellationToken cancellationToken)
    {
        var payment = await LoadTrackedAsync(paymentId, reload: false, cancellationToken);
        var reversed = payment.Reverse(isSettled, reason, clock.GetUtcNow(), actor, correlation.CorrelationId);
        return reversed.IsSuccess ? Snapshot(payment) : reversed.Error;
    }

    private async Task<Payment> LoadTrackedAsync(Guid paymentId, bool reload, CancellationToken ct)
    {
        var tracked = db.Set<Payment>().Local.SingleOrDefault(p => p.Id == paymentId);
        if (tracked is not null)
        {
            if (reload)
            {
                await db.Entry(tracked).ReloadAsync(ct);
            }

            return tracked;
        }

        if (db.Database.CurrentTransaction is null)
        {
            throw new InvalidOperationException("Changing a payment's refund position requires LockAsync inside a transaction first.");
        }

        return await db.Set<Payment>().SingleAsync(p => p.Id == paymentId, ct);
    }

    private static PaymentSnapshot Snapshot(Payment p) => new(
        p.Id, p.MerchantId, p.Status, p.Amount, p.Currency, p.FeeAmount, p.RefundedAmount, p.PendingRefundAmount, p.PaymentMethod);
}
