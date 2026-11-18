using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using PayFlow.Infrastructure.Persistence;
using PayFlow.Modules.Audit.Contracts;
using PayFlow.Modules.Ledger.Contracts;
using PayFlow.Modules.Payments.Contracts;
using PayFlow.Modules.Refunds.Contracts;
using PayFlow.Modules.Reconciliation.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Money;
using PayFlow.SharedKernel.Results;
using PayFlow.SharedKernel.Security;

namespace PayFlow.Modules.Reconciliation.Application;

public sealed record ImportStatementRequest(string SourceName, DateOnly PeriodStart, DateOnly PeriodEnd, string FileName, string Csv);

public sealed record StatementResponse(
    string Id,
    string SourceName,
    DateOnly PeriodStart,
    DateOnly PeriodEnd,
    string FileName,
    string FileSha256,
    int LineCount,
    StatementStatus Status,
    string ImportedBy,
    DateTimeOffset ImportedAt,
    string? LastRunId);

public sealed record StatementPage(IReadOnlyList<StatementResponse> Items, int Page, int PageSize, int Total);

public sealed record OutcomeCounts(int Matched, int MissingExternal, int MissingInternal, int AmountMismatch, int CurrencyMismatch, int DuplicateExternal, int StatusDiscrepancy);

public sealed record RunResponse(string Id, string StatementId, DateTimeOffset StartedAt, DateTimeOffset CompletedAt, string StartedBy, OutcomeCounts Counts, int ExceptionsCreated, int ExceptionsSeenAgain);

public sealed record ResultResponse(
    ReconciliationOutcome Outcome,
    int? LineNumber,
    string? InternalType,
    string? InternalId,
    string? Reference,
    string? ExternalId,
    string? InternalAmount,
    string? InternalCurrency,
    string? ExternalAmount,
    string? ExternalCurrency,
    string? InternalStatus,
    string? ExternalStatus,
    string Detail);

public sealed record ResultPage(IReadOnlyList<ResultResponse> Items, int Page, int PageSize, int Total);

public sealed record ExceptionHistoryResponse(ExceptionStatus? From, ExceptionStatus To, string? Note, string Actor, DateTimeOffset OccurredAt);

public sealed record ExceptionResponse(
    string Id,
    ReconciliationOutcome Outcome,
    ExceptionStatus Status,
    string StatementId,
    string? Reference,
    string? ExternalId,
    string? InternalType,
    string? InternalId,
    string? InternalAmount,
    string? InternalCurrency,
    string? ExternalAmount,
    string? ExternalCurrency,
    string Detail,
    string? AssignedTo,
    ResolutionType? ResolutionType,
    string? ResolutionNote,
    string? AdjustmentAmount,
    AdjustmentDirection? AdjustmentDirection,
    string? ResolvedBy,
    DateTimeOffset? ResolvedAt,
    int TimesSeen,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<ExceptionHistoryResponse>? History);

public sealed record ExceptionPage(IReadOnlyList<ExceptionResponse> Items, int Page, int PageSize, int Total);

public sealed record AdjustmentRequest(string Amount, AdjustmentDirection Direction);

public sealed record ResolveExceptionRequest(ResolutionType? ResolutionType, string Note, AdjustmentRequest? Adjustment);

public sealed record ExceptionNoteRequest(string? Note);

/// <summary>Statement import, matching runs and the exception workflow (REC-1..5, docs/RECONCILIATION.md).</summary>
internal sealed class ReconciliationService(
    PayFlowDbContext db,
    IPaymentLookup payments,
    IRefundLookup refunds,
    ILedger ledger,
    IAuditLog audit,
    IActorContext actor,
    IdGenerator ids,
    TimeProvider clock)
{
    public const string AdjustmentSourceType = "reconciliation_exception";
    private const string StatementTarget = "reconciliation_statement";
    private const string ExceptionTarget = "reconciliation_exception";

    private string ActorName => actor.Actor.Id ?? actor.Actor.Display;

    public async Task<Result<StatementResponse>> ImportAsync(ImportStatementRequest request, CancellationToken ct)
    {
        if (request.PeriodEnd < request.PeriodStart)
        {
            return Error.Validation("invalid_period", "periodEnd must not be before periodStart.");
        }

        var (lines, errors) = StatementCsv.Parse(request.Csv);
        if (errors.Count > 0)
        {
            return Error.Validation("invalid_statement", "The statement file has errors; nothing was imported.", errors);
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(request.Csv));
        if (await db.Set<Statement>().AnyAsync(s => s.FileSha256 == hash, ct))
        {
            return AlreadyImported();
        }

        var statement = new Statement
        {
            Id = ids.NewId(),
            SourceName = request.SourceName.Trim(),
            PeriodStart = request.PeriodStart,
            PeriodEnd = request.PeriodEnd,
            FileName = request.FileName.Trim(),
            FileSha256 = hash,
            LineCount = lines.Count,
            Status = StatementStatus.Imported,
            ImportedBy = ActorName,
            ImportedAt = clock.GetUtcNow(),
        };
        db.Set<Statement>().Add(statement);
        db.Set<StatementLine>().AddRange(lines.Select(l => new StatementLine
        {
            StatementId = statement.Id,
            LineNumber = l.LineNumber,
            ExternalId = l.ExternalId,
            Reference = l.Reference,
            TransactionType = l.Type,
            Amount = l.Amount,
            Currency = l.Currency,
            ExternalStatus = l.Status,
            ValueDate = l.ValueDate,
            Raw = l.Raw,
        }));
        audit.Record(new AuditEntry("reconciliation.statement.imported", StatementTarget, PublicId.Create(IdPrefixes.Statement, statement.Id))
        {
            Changes = new { statement.SourceName, statement.FileName, statement.LineCount, statement.PeriodStart, statement.PeriodEnd },
        });

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return AlreadyImported(); // a concurrent import of the same file won
        }

        return ToResponse(statement);
    }

    public async Task<StatementPage> ListStatementsAsync(int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Paging(page, pageSize);
        var query = db.Set<Statement>().AsNoTracking();
        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(s => s.ImportedAt).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new StatementPage(rows.Select(ToResponse).ToList(), page, pageSize, total);
    }

    public async Task<Result<StatementResponse>> GetStatementAsync(Guid statementId, CancellationToken ct)
    {
        var statement = await db.Set<Statement>().AsNoTracking().SingleOrDefaultAsync(s => s.Id == statementId, ct);
        return statement is null ? StatementNotFound() : ToResponse(statement);
    }

    /// <summary>
    /// Matches a statement against PayFlow's records (REC-3). Safe to repeat: results are stored per
    /// run, and exceptions are keyed by fingerprint so a rerun updates rather than duplicates (REC-5).
    /// </summary>
    public async Task<Result<RunResponse>> RunAsync(Guid statementId, CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"reconciliation:{statementId:N}";
        await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);

        var statement = await db.Set<Statement>().SingleOrDefaultAsync(s => s.Id == statementId, ct);
        if (statement is null)
        {
            return StatementNotFound();
        }

        var lines = await db.Set<StatementLine>().AsNoTracking().Where(l => l.StatementId == statementId).OrderBy(l => l.LineNumber).ToListAsync(ct);
        var paymentIds = ReferencedIds(lines, StatementTransactionType.PAYMENT, IdPrefixes.Payment);
        var refundIds = ReferencedIds(lines, StatementTransactionType.REFUND, IdPrefixes.Refund);
        var engineResults = ReconciliationEngine.Match(
            lines,
            await payments.FindAsync(paymentIds, ct),
            await refunds.FindAsync(refundIds, ct),
            await payments.ListAcquirerVisibleAsync(statement.PeriodStartUtc, statement.PeriodEndExclusiveUtc, ct),
            await refunds.ListAcquirerVisibleAsync(statement.PeriodStartUtc, statement.PeriodEndExclusiveUtc, ct));

        var now = clock.GetUtcNow();
        var run = new ReconciliationRun { Id = ids.NewId(), StatementId = statementId, StartedAt = now, CompletedAt = now, StartedBy = ActorName };
        var counts = engineResults.GroupBy(r => r.Outcome).ToDictionary(g => g.Key, g => g.Count());
        run.Matched = counts.GetValueOrDefault(ReconciliationOutcome.Matched);
        run.MissingExternal = counts.GetValueOrDefault(ReconciliationOutcome.MissingExternal);
        run.MissingInternal = counts.GetValueOrDefault(ReconciliationOutcome.MissingInternal);
        run.AmountMismatch = counts.GetValueOrDefault(ReconciliationOutcome.AmountMismatch);
        run.CurrencyMismatch = counts.GetValueOrDefault(ReconciliationOutcome.CurrencyMismatch);
        run.DuplicateExternal = counts.GetValueOrDefault(ReconciliationOutcome.DuplicateExternal);
        run.StatusDiscrepancy = counts.GetValueOrDefault(ReconciliationOutcome.StatusDiscrepancy);
        db.Set<ReconciliationRun>().Add(run);

        var stored = engineResults.Select(r => (Engine: r, Row: new ReconciliationResult
        {
            RunId = run.Id,
            Outcome = r.Outcome,
            StatementLineId = r.Line?.Id,
            LineNumber = r.Line?.LineNumber,
            InternalType = r.InternalType,
            InternalId = r.InternalId,
            Reference = r.Line?.Reference ?? (r.InternalId is { } id ? PublicId.Create(r.InternalType == ReconciliationEngine.RefundType ? IdPrefixes.Refund : IdPrefixes.Payment, id) : null),
            ExternalId = r.Line?.ExternalId,
            InternalAmount = r.InternalAmount,
            ExternalAmount = r.Line?.Amount,
            InternalCurrency = r.InternalCurrency,
            ExternalCurrency = r.Line?.Currency,
            InternalStatus = r.InternalStatus,
            ExternalStatus = r.Line?.ExternalStatus,
            Detail = r.Detail,
        })).ToList();
        db.Set<ReconciliationResult>().AddRange(stored.Select(s => s.Row));
        await db.SaveChangesAsync(ct); // result ids are needed by the exceptions

        var discrepancies = stored
            .Where(s => s.Engine.Outcome != ReconciliationOutcome.Matched)
            .Select(s => (s.Row, Fingerprint: ReconciliationEngine.Fingerprint(statementId, s.Engine)))
            .ToList();
        var fingerprints = discrepancies.Select(d => d.Fingerprint).ToList();
        var existing = (await db.Set<ReconciliationException>().Where(x => fingerprints.Contains(x.Fingerprint)).ToListAsync(ct))
            .ToDictionary(x => Convert.ToHexString(x.Fingerprint), StringComparer.Ordinal);

        var created = 0;
        var seenAgain = 0;
        foreach (var (row, fingerprint) in discrepancies)
        {
            if (existing.TryGetValue(Convert.ToHexString(fingerprint), out var known))
            {
                known.SeenAgain(row, now);
                seenAgain++;
                continue;
            }

            var exception = ReconciliationException.Raise(ids.NewId(), fingerprint, statementId, row, now);
            db.Set<ReconciliationException>().Add(exception);
            db.Set<ExceptionHistoryEntry>().Add(new ExceptionHistoryEntry
            {
                ExceptionId = exception.Id,
                FromStatus = null,
                ToStatus = ExceptionStatus.Open,
                Note = $"Raised by run {PublicId.Create(IdPrefixes.ReconciliationRun, run.Id)}: {row.Detail}",
                Actor = ActorName,
                OccurredAt = now,
            });
            created++;
        }

        run.ExceptionsCreated = created;
        run.CompletedAt = clock.GetUtcNow();
        statement.Status = StatementStatus.Reconciled;
        statement.LastRunId = run.Id;
        audit.Record(new AuditEntry("reconciliation.run.completed", StatementTarget, PublicId.Create(IdPrefixes.Statement, statementId))
        {
            Changes = new { run = PublicId.Create(IdPrefixes.ReconciliationRun, run.Id), counts, exceptionsCreated = created, exceptionsSeenAgain = seenAgain },
        });

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return ToResponse(run, seenAgain);
    }

    public async Task<Result<RunResponse>> GetRunAsync(Guid runId, CancellationToken ct)
    {
        var run = await db.Set<ReconciliationRun>().AsNoTracking().SingleOrDefaultAsync(r => r.Id == runId, ct);
        if (run is null)
        {
            return RunNotFound();
        }

        var seenAgain = run.MissingExternal + run.MissingInternal + run.AmountMismatch + run.CurrencyMismatch + run.DuplicateExternal + run.StatusDiscrepancy - run.ExceptionsCreated;
        return ToResponse(run, seenAgain);
    }

    public async Task<Result<ResultPage>> ListResultsAsync(Guid runId, ReconciliationOutcome? outcome, int page, int pageSize, CancellationToken ct)
    {
        if (!await db.Set<ReconciliationRun>().AnyAsync(r => r.Id == runId, ct))
        {
            return RunNotFound();
        }

        (page, pageSize) = Paging(page, pageSize);
        var query = db.Set<ReconciliationResult>().AsNoTracking().Where(r => r.RunId == runId);
        if (outcome is { } o)
        {
            query = query.Where(r => r.Outcome == o);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new ResultPage(rows.Select(ToResponse).ToList(), page, pageSize, total);
    }

    public async Task<ExceptionPage> ListExceptionsAsync(ExceptionStatus? status, ReconciliationOutcome? outcome, Guid? statementId, int page, int pageSize, CancellationToken ct)
    {
        (page, pageSize) = Paging(page, pageSize);
        var query = db.Set<ReconciliationException>().AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(x => x.Status == s);
        }

        if (outcome is { } o)
        {
            query = query.Where(x => x.Outcome == o);
        }

        if (statementId is { } st)
        {
            query = query.Where(x => x.StatementId == st);
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new ExceptionPage(rows.Select(x => ToResponse(x, null)).ToList(), page, pageSize, total);
    }

    public async Task<Result<ExceptionResponse>> GetExceptionAsync(Guid exceptionId, CancellationToken ct)
    {
        var exception = await db.Set<ReconciliationException>().AsNoTracking().SingleOrDefaultAsync(x => x.Id == exceptionId, ct);
        return exception is null ? ExceptionNotFound() : ToResponse(exception, await HistoryAsync(exceptionId, ct));
    }

    public Task<Result<ExceptionResponse>> StartReviewAsync(Guid exceptionId, ExceptionNoteRequest request, CancellationToken ct) =>
        TransitionAsync(exceptionId, request.Note, "reconciliation.exception.review_started", (x, now) => x.StartReview(ActorName, now), ct);

    public Task<Result<ExceptionResponse>> DismissAsync(Guid exceptionId, ExceptionNoteRequest request, CancellationToken ct) =>
        TransitionAsync(exceptionId, request.Note, "reconciliation.exception.dismissed", (x, now) => x.Dismiss(request.Note ?? string.Empty, ActorName, now), ct);

    /// <summary>
    /// Resolves an exception. With an adjustment, a <c>ReconciliationAdjustment</c> journal entry
    /// moves the amount between reconciliation suspense and the acquirer receivable, in the same
    /// transaction; the ledger's unique source key allows one adjustment per exception.
    /// </summary>
    public Task<Result<ExceptionResponse>> ResolveAsync(Guid exceptionId, ResolveExceptionRequest request, CancellationToken ct)
    {
        if (request.Adjustment is null)
        {
            if (request.ResolutionType is null)
            {
                return Task.FromResult<Result<ExceptionResponse>>(Error.Validation("resolution_type_required", "resolutionType is required."));
            }

            return TransitionAsync(exceptionId, request.Note, "reconciliation.exception.resolved",
                (x, now) => x.Resolve(request.ResolutionType.Value, request.Note, ActorName, now), ct);
        }

        if (request.ResolutionType is not (null or ResolutionType.AdjustmentPosted))
        {
            return Task.FromResult<Result<ExceptionResponse>>(Error.Validation("invalid_resolution_type", "An adjustment can only be posted with resolutionType AdjustmentPosted."));
        }

        return TransitionAsync(exceptionId, request.Note, "reconciliation.exception.adjusted", async (x, now) =>
        {
            if (!Money.TryParse(request.Adjustment.Amount, Currency.FromCode(x.Currency), out var amount) || !amount.IsPositive)
            {
                return Error.Validation("invalid_adjustment_amount", $"The adjustment must be a positive {x.Currency} amount.");
            }

            var closed = x.ResolveWithAdjustment(amount.Amount, request.Adjustment.Direction, request.Note, ActorName, now);
            if (!closed.IsSuccess)
            {
                return closed;
            }

            var (debit, credit) = request.Adjustment.Direction == AdjustmentDirection.IncreaseReceivable
                ? (LedgerAccountKind.AcquirerReceivable, LedgerAccountKind.ReconciliationSuspense)
                : (LedgerAccountKind.ReconciliationSuspense, LedgerAccountKind.AcquirerReceivable);
            await ledger.PostAsync(
                new JournalEntryRequest(
                    JournalEntryTypes.ReconciliationAdjustment, AdjustmentSourceType, x.Id, x.Currency,
                    $"Reconciliation adjustment for {PublicId.Create(IdPrefixes.ReconciliationException, x.Id)} ({x.Outcome})",
                    [
                        new PostingRequest(debit, null, PostingDirection.Debit, amount.Amount),
                        new PostingRequest(credit, null, PostingDirection.Credit, amount.Amount),
                    ]),
                ct);
            return Result.Success();
        }, ct);
    }

    private Task<Result<ExceptionResponse>> TransitionAsync(Guid exceptionId, string? note, string action, Func<ReconciliationException, DateTimeOffset, Result> change, CancellationToken ct) =>
        TransitionAsync(exceptionId, note, action, (x, now) => Task.FromResult(change(x, now)), ct);

    private async Task<Result<ExceptionResponse>> TransitionAsync(
        Guid exceptionId, string? note, string action, Func<ReconciliationException, DateTimeOffset, Task<Result>> change, CancellationToken ct)
    {
        try
        {
            await using var transaction = await db.Database.BeginTransactionAsync(ct);
            // Row lock first, then a tracked load: concurrent resolutions queue instead of racing.
            await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM reconciliation.exceptions WHERE id = {exceptionId} FOR UPDATE").ToListAsync(ct);
            var exception = await db.Set<ReconciliationException>().SingleOrDefaultAsync(x => x.Id == exceptionId, ct);
            if (exception is null)
            {
                return ExceptionNotFound();
            }

            var from = exception.Status;
            var now = clock.GetUtcNow();
            var changed = await change(exception, now);
            if (!changed.IsSuccess)
            {
                return changed.Error;
            }

            db.Set<ExceptionHistoryEntry>().Add(new ExceptionHistoryEntry
            {
                ExceptionId = exception.Id,
                FromStatus = from,
                ToStatus = exception.Status,
                Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
                Actor = ActorName,
                OccurredAt = now,
            });
            audit.Record(new AuditEntry(action, ExceptionTarget, PublicId.Create(IdPrefixes.ReconciliationException, exception.Id))
            {
                Changes = new
                {
                    from,
                    to = exception.Status,
                    exception.ResolutionType,
                    exception.AdjustmentAmount,
                    exception.AdjustmentDirection,
                    currency = exception.Currency,
                },
            });

            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return ToResponse(exception, await HistoryAsync(exception.Id, ct));
        }
        catch (DbUpdateConcurrencyException)
        {
            return Error.Conflict("concurrent_update", "The exception was changed by someone else; reload and try again.");
        }
    }

    private async Task<IReadOnlyList<ExceptionHistoryResponse>> HistoryAsync(Guid exceptionId, CancellationToken ct) =>
        (await db.Set<ExceptionHistoryEntry>().AsNoTracking().Where(h => h.ExceptionId == exceptionId).OrderBy(h => h.Id).ToListAsync(ct))
        .Select(h => new ExceptionHistoryResponse(h.FromStatus, h.ToStatus, h.Note, h.Actor, h.OccurredAt))
        .ToList();

    private static List<Guid> ReferencedIds(IEnumerable<StatementLine> lines, StatementTransactionType type, string prefix) =>
        lines.Where(l => l.TransactionType == type)
            .Select(l => PublicId.TryParse(l.Reference, prefix, out var id) ? id.Value : (Guid?)null)
            .OfType<Guid>()
            .Distinct()
            .ToList();

    private static (int Page, int PageSize) Paging(int page, int pageSize) => (Math.Max(page, 1), Math.Clamp(pageSize, 1, 100));

    internal static string? FormatAmount(decimal? amount, string? currency) =>
        amount is { } value ? ReconciliationEngine.Format(value, currency ?? string.Empty) : null;

    private static StatementResponse ToResponse(Statement s) => new(
        PublicId.Create(IdPrefixes.Statement, s.Id),
        s.SourceName,
        s.PeriodStart,
        s.PeriodEnd,
        s.FileName,
        Convert.ToHexStringLower(s.FileSha256),
        s.LineCount,
        s.Status,
        s.ImportedBy,
        s.ImportedAt,
        s.LastRunId is { } r ? PublicId.Create(IdPrefixes.ReconciliationRun, r) : null);

    private static RunResponse ToResponse(ReconciliationRun r, int seenAgain) => new(
        PublicId.Create(IdPrefixes.ReconciliationRun, r.Id),
        PublicId.Create(IdPrefixes.Statement, r.StatementId),
        r.StartedAt,
        r.CompletedAt,
        r.StartedBy,
        new OutcomeCounts(r.Matched, r.MissingExternal, r.MissingInternal, r.AmountMismatch, r.CurrencyMismatch, r.DuplicateExternal, r.StatusDiscrepancy),
        r.ExceptionsCreated,
        seenAgain);

    private static ResultResponse ToResponse(ReconciliationResult r) => new(
        r.Outcome,
        r.LineNumber,
        r.InternalType,
        InternalPublicId(r.InternalType, r.InternalId),
        r.Reference,
        r.ExternalId,
        FormatAmount(r.InternalAmount, r.InternalCurrency),
        r.InternalCurrency,
        FormatAmount(r.ExternalAmount, r.ExternalCurrency),
        r.ExternalCurrency,
        r.InternalStatus,
        r.ExternalStatus,
        r.Detail);

    private static ExceptionResponse ToResponse(ReconciliationException x, IReadOnlyList<ExceptionHistoryResponse>? history) => new(
        PublicId.Create(IdPrefixes.ReconciliationException, x.Id),
        x.Outcome,
        x.Status,
        PublicId.Create(IdPrefixes.Statement, x.StatementId),
        x.Reference,
        x.ExternalId,
        x.InternalType,
        InternalPublicId(x.InternalType, x.InternalId),
        FormatAmount(x.InternalAmount, x.InternalCurrency),
        x.InternalCurrency,
        FormatAmount(x.ExternalAmount, x.ExternalCurrency),
        x.ExternalCurrency,
        x.Detail,
        x.AssignedTo,
        x.ResolutionType,
        x.ResolutionNote,
        FormatAmount(x.AdjustmentAmount, x.Currency),
        x.AdjustmentDirection,
        x.ResolvedBy,
        x.ResolvedAt,
        x.TimesSeen,
        x.CreatedAt,
        x.UpdatedAt,
        history);

    private static string? InternalPublicId(string? type, Guid? id) => id is { } value
        ? PublicId.Create(type == ReconciliationEngine.RefundType ? IdPrefixes.Refund : IdPrefixes.Payment, value)
        : null;

    private static Error AlreadyImported() => Error.Conflict("statement_already_imported", "This statement file has already been imported.");

    private static Error StatementNotFound() => Error.NotFound("statement_not_found", "Statement not found.");

    private static Error RunNotFound() => Error.NotFound("reconciliation_run_not_found", "Reconciliation run not found.");

    private static Error ExceptionNotFound() => Error.NotFound("reconciliation_exception_not_found", "Reconciliation exception not found.");
}
