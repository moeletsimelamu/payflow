using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Infrastructure.Telemetry;
using PayFlow.Infrastructure.Workers;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Refunds.Application;

/// <summary>
/// Completes pending refunds with the simulated processor's answer (rule R5). For each refund, one
/// transaction locks the payment row (the same lock refund creation takes), resolves the refund,
/// moves the payment's refunded/pending amounts and status, posts the ledger entry and writes the
/// outbox event.
/// </summary>
internal sealed class RefundProcessingJob(IServiceScopeFactory scopes, PayFlowDbContext db, ILogger<RefundProcessingJob> logger) : IRecurringJob
{
    private static readonly Actor Actor = Actor.System("refund-processor");

    public string Name => "refund-processing";

    public async Task<int> RunOnceAsync(CancellationToken cancellationToken)
    {
        var pending = await db.Set<Refund>().AsNoTracking()
            .Where(r => r.Status == RefundStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .Select(r => new { r.Id, r.PaymentId, r.MerchantId })
            .Take(50)
            .ToListAsync(cancellationToken);

        var handled = 0;
        foreach (var item in pending)
        {
            await using var scope = scopes.CreateAsyncScope();
            var sp = scope.ServiceProvider;
            var scopedDb = sp.GetRequiredService<PayFlowDbContext>();
            var payments = sp.GetRequiredService<IPaymentRefundOperations>();
            var ledger = sp.GetRequiredService<ILedger>();
            var outbox = sp.GetRequiredService<IOutbox>();
            var clock = sp.GetRequiredService<TimeProvider>();
            using var activity = PayFlowTelemetry.StartWork("refund.process");

            await using var transaction = await scopedDb.Database.BeginTransactionAsync(cancellationToken);
            await payments.LockAsync(item.MerchantId, item.PaymentId, cancellationToken);
            var refund = await scopedDb.Set<Refund>().SingleAsync(r => r.Id == item.Id, cancellationToken);
            if (refund.Status != RefundStatus.Pending)
            {
                continue; // another instance completed it while we waited for the lock
            }

            var now = clock.GetUtcNow();
            var succeeded = RefundSimulator.Succeeds(refund.PaymentMethod);
            if (succeeded)
            {
                refund.Succeed(now);
                await ledger.PostAsync(
                    new JournalEntryRequest(
                        JournalEntryTypes.RefundSucceeded, RefundService.RefundResource, refund.Id, refund.Currency, "Refund to customer (simulated)",
                        [
                            new PostingRequest(LedgerAccountKind.MerchantPayable, refund.MerchantId, PostingDirection.Debit, refund.Amount),
                            new PostingRequest(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Credit, refund.Amount),
                        ]),
                    cancellationToken);
            }
            else
            {
                refund.Fail(RefundSimulator.FailureReason, now);
            }

            await payments.CompleteRefundAsync(refund.PaymentId, refund.Amount, succeeded, Actor, cancellationToken);
            outbox.Enqueue(succeeded ? RefundEventTypes.Succeeded : RefundEventTypes.Failed, refund.ToEvent(now));
            await scopedDb.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            PayFlowTelemetry.RefundOutcomes.Add(1, PayFlowTelemetry.Tag("payflow.refund.status", refund.Status.ToString()));
            activity?.SetTag("payflow.refund.status", refund.Status.ToString());
            logger.LogInformation("Refund {RefundId} {Status}.", PublicId.Create(IdPrefixes.Refund, refund.Id), refund.Status);
            handled++;
        }

        return handled;
    }
}

/// <summary>Until Phase 7 adds settlement, no payment is ever settled.</summary>
internal sealed class NoSettlementsYet : ISettlementLookup
{
    public Task<bool> IsPaymentSettledAsync(Guid paymentId, CancellationToken cancellationToken) => Task.FromResult(false);
}
