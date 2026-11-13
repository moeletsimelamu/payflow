using System.Text.Json;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;
using PayFlow.Infrastructure.Idempotency;
using PayFlow.Infrastructure.Outbox;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Merchants.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Refunds.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Refunds.Application;

/// <summary>
/// Refund creation and reversal (REF-1..6). Both are single transactions after the idempotency claim:
/// lock the payment row, check and change its refund position through
/// <see cref="IPaymentRefundOperations"/>, record the new row, store the idempotent response, commit.
/// Nothing external happens synchronously; refunds are completed by <see cref="RefundProcessingJob"/>.
/// </summary>
internal sealed class RefundService(
    PayFlowDbContext db,
    IdempotencyStore idempotency,
    IPaymentRefundOperations payments,
    IMerchantDirectory merchants,
    ISettlementLookup settlements,
    ILedger ledger,
    IOutbox outbox,
    IAuditLog audit,
    IActorContext actorContext,
    IdGenerator ids,
    TimeProvider clock,
    IOptions<JsonOptions> json)
{
    public const string RefundOperation = "refunds.create";
    public const string ReversalOperation = "reversals.create";
    public const string RefundResource = "refund";
    public const string ReversalResource = "reversal";

    public async Task<Result<IdempotentResponse>> RefundAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey, CreateRefundRequest request, RefundChannel channel, string route, CancellationToken ct)
    {
        return await IdempotentlyAsync(merchantId, RefundOperation, idempotencyKey, route, request, async key =>
        {
            var payment = await payments.LockAsync(merchantId, paymentId, ct);
            if (payment is null)
            {
                return PaymentNotFound();
            }

            var currency = Currency.FromCode(payment.Currency);
            decimal amount;
            if (request.Amount is null)
            {
                amount = payment.RefundableAmount;
            }
            else if (Money.TryParse(request.Amount, currency, out var parsed) && parsed.IsPositive)
            {
                amount = parsed.Amount;
            }
            else
            {
                return Error.Validation("invalid_amount", $"amount must be a positive decimal string with at most {currency.MinorUnits} decimal places for {currency.Code}.");
            }

            var limit = await CheckOperatorLimitAsync(merchantId, payment.Currency, amount, channel, ct);
            if (!limit.IsSuccess)
            {
                return limit.Error;
            }

            var reserved = await payments.ReserveRefundAsync(paymentId, amount, ct);
            if (!reserved.IsSuccess)
            {
                return reserved.Error;
            }

            var actor = actorContext.Actor;
            var refund = Refund.Request(ids.NewId(), paymentId, merchantId, amount, payment.Currency, request.Reason?.Trim(), payment.PaymentMethod, key, actor, clock.GetUtcNow());
            db.Set<Refund>().Add(refund);
            var refundId = PublicId.Create(IdPrefixes.Refund, refund.Id);
            audit.Record(new AuditEntry("payment.refund.requested", RefundResource, refundId)
            {
                MerchantId = merchantId,
                Changes = new { paymentId = PublicId.Create(IdPrefixes.Payment, paymentId), amount = Money.Of(amount, currency).ToAmountString(), currency = currency.Code, request.Reason, channel = channel.ToString() },
            });
            await db.SaveChangesAsync(ct);
            return new Created(RefundResource, refund.Id, Serialize(refund.ToResponse()), $"/v1/refunds/{refundId}");
        }, ct);
    }

    public async Task<Result<IdempotentResponse>> ReverseAsync(
        Guid merchantId, Guid paymentId, string? idempotencyKey, CreateReversalRequest request, string route, CancellationToken ct)
    {
        return await IdempotentlyAsync(merchantId, ReversalOperation, idempotencyKey, route, request, async key =>
        {
            var payment = await payments.LockAsync(merchantId, paymentId, ct);
            if (payment is null)
            {
                return PaymentNotFound();
            }

            var actor = actorContext.Actor;
            var reason = string.IsNullOrWhiteSpace(request.Reason) ? "Reversed at the merchant's request." : request.Reason.Trim();
            var isSettled = await settlements.IsPaymentSettledAsync(paymentId, ct);
            var reversed = await payments.ReverseAsync(paymentId, isSettled, reason, actor, ct);
            if (!reversed.IsSuccess)
            {
                return reversed.Error;
            }

            var reversal = new Reversal
            {
                Id = ids.NewId(),
                PaymentId = paymentId,
                MerchantId = merchantId,
                Amount = payment.Amount,
                FeeReturned = payment.Fee ?? 0m,
                Currency = payment.Currency,
                Reason = reason,
                RequestedByType = actor.Type.ToString(),
                RequestedById = actor.Id,
                IdempotencyKey = key,
                CreatedAt = clock.GetUtcNow(),
            };
            db.Set<Reversal>().Add(reversal);
            await PostReversalAsync(reversal, ct);
            outbox.Enqueue(PaymentEventTypes.Reversed, reversal.ToEvent());
            var reversalId = PublicId.Create(IdPrefixes.Reversal, reversal.Id);
            audit.Record(new AuditEntry("payment.reversed", ReversalResource, reversalId)
            {
                MerchantId = merchantId,
                Changes = new { paymentId = PublicId.Create(IdPrefixes.Payment, paymentId), amount = reversal.Amount, feeReturned = reversal.FeeReturned, reason },
            });
            await db.SaveChangesAsync(ct);
            return new Created(ReversalResource, reversal.Id, Serialize(reversal.ToResponse()), null);
        }, ct);
    }

    private sealed record Created(string ResourceType, Guid ResourceId, string Body, string? Location);

    /// <summary>
    /// Claim → one transaction (work + link + stored response) → commit. Any failure inside rolls the
    /// transaction back, so nothing was executed and the key is released for a corrected retry.
    /// </summary>
    private async Task<Result<IdempotentResponse>> IdempotentlyAsync<TRequest>(
        Guid merchantId, string operation, string? idempotencyKey, string route, TRequest request, Func<string, Task<Result<Created>>> work, CancellationToken ct)
        where TRequest : class
    {
        var key = IdempotencyKeys.Validate(idempotencyKey);
        if (!key.IsSuccess)
        {
            return key.Error;
        }

        var keyRef = new IdempotencyKeyRef($"merchant:{merchantId:N}", operation, key.Value);
        var claim = await idempotency.ClaimAsync(keyRef, IdempotencyKeys.HashRequest("POST", route, request, json.Value.SerializerOptions), ct);
        switch (claim.Kind)
        {
            case IdempotencyClaimKind.Mismatch:
                return Error.Unprocessable("idempotency_key_reused", "This Idempotency-Key was already used with a different request.");
            case IdempotencyClaimKind.InProgress:
                return Error.Conflict("idempotency_request_in_progress", "A request with this Idempotency-Key is still being processed. Retry shortly.");
            case IdempotencyClaimKind.Replay:
                return new IdempotentResponse(claim.ResponseStatus!.Value, claim.ResponseBody!, Replayed: true, Location: null);
        }

        Result<Created> outcome;
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            outcome = await work(key.Value);
            if (outcome.IsSuccess)
            {
                var created = outcome.Value;
                await idempotency.AttachResourceAsync(keyRef, created.ResourceType, created.ResourceId, ct);
                await idempotency.CompleteForResourceAsync(created.ResourceType, created.ResourceId, 201, created.Body, ct);
                await transaction.CommitAsync(ct);
            }
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } pg &&
                                           pg.ConstraintName?.EndsWith("_merchant_id_idempotency_key", StringComparison.Ordinal) == true)
        {
            await idempotency.ReleaseAsync(keyRef);
            return Error.Conflict("duplicate_idempotency_key", "This Idempotency-Key was already used for an earlier request.");
        }
        catch
        {
            await idempotency.ReleaseAsync(keyRef);
            throw;
        }

        if (!outcome.IsSuccess)
        {
            await idempotency.ReleaseAsync(keyRef);
            return outcome.Error;
        }

        return new IdempotentResponse(201, outcome.Value.Body, Replayed: false, outcome.Value.Location);
    }

    private async Task<Result> CheckOperatorLimitAsync(Guid merchantId, string currency, decimal amount, RefundChannel channel, CancellationToken ct)
    {
        if (channel != RefundChannel.MerchantOperator)
        {
            return Result.Success();
        }

        var profile = await merchants.GetPaymentProfileAsync(merchantId, currency, ct);
        var limit = profile?.Terms?.OperatorRefundLimit;
        if (limit is null)
        {
            return Error.Forbidden("operator_refund_not_permitted", $"Merchant operators may not refund {currency} payments. Ask a MerchantAdmin.");
        }

        return amount > limit
            ? Error.Forbidden("operator_refund_limit_exceeded", $"Operators may refund at most {Money.Of(limit.Value, currency)} per refund. Ask a MerchantAdmin.")
            : Result.Success();
    }

    /// <summary>docs/PAYMENT_LIFECYCLE.md §5: reverse the capture, and return the fee to the merchant.</summary>
    private async Task PostReversalAsync(Reversal reversal, CancellationToken ct)
    {
        await ledger.PostAsync(
            new JournalEntryRequest(
                JournalEntryTypes.PaymentReversed, ReversalResource, reversal.Id, reversal.Currency, "Payment reversed before settlement",
                [
                    new PostingRequest(LedgerAccountKind.MerchantPayable, reversal.MerchantId, PostingDirection.Debit, reversal.Amount),
                    new PostingRequest(LedgerAccountKind.AcquirerReceivable, null, PostingDirection.Credit, reversal.Amount),
                ]),
            ct);
        if (reversal.FeeReturned > 0m)
        {
            await ledger.PostAsync(
                new JournalEntryRequest(
                    JournalEntryTypes.ReversalFeeReturned, ReversalResource, reversal.Id, reversal.Currency, "Processing fee returned on reversal",
                    [
                        new PostingRequest(LedgerAccountKind.FeeRevenue, null, PostingDirection.Debit, reversal.FeeReturned),
                        new PostingRequest(LedgerAccountKind.MerchantPayable, reversal.MerchantId, PostingDirection.Credit, reversal.FeeReturned),
                    ]),
                ct);
        }
    }

    private string Serialize<T>(T value) => JsonSerializer.Serialize(value, json.Value.SerializerOptions);

    private static Error PaymentNotFound() => Error.NotFound("payment_not_found", "Payment not found.");
}
