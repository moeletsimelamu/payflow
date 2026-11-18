using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Reconciliation.Domain;

public enum ReconciliationOutcome
{
    Matched,
    MissingExternal,
    MissingInternal,
    AmountMismatch,
    CurrencyMismatch,
    DuplicateExternal,
    StatusDiscrepancy,
}

public enum StatementTransactionType
{
    PAYMENT,
    REFUND,
}

public enum StatementStatus
{
    Imported,
    Reconciled,
}

public enum ExceptionStatus
{
    Open,
    InReview,
    Resolved,
    Dismissed,
}

public enum ResolutionType
{
    /// <summary>The acquirer is right; internal records stand corrected by an adjustment or follow-up.</summary>
    AcceptedExternal,

    /// <summary>PayFlow's records are right; the statement is wrong.</summary>
    AcceptedInternal,

    /// <summary>A ledger adjustment was posted to reconciliation suspense.</summary>
    AdjustmentPosted,

    ExternalCorrectionRequested,

    FalsePositive,
}

/// <summary>Which way an approved adjustment moves the acquirer receivable.</summary>
public enum AdjustmentDirection
{
    /// <summary>The acquirer owes more than PayFlow recorded: Debit acquirer_receivable, Credit reconciliation_suspense.</summary>
    IncreaseReceivable,

    /// <summary>The acquirer owes less than PayFlow recorded: Debit reconciliation_suspense, Credit acquirer_receivable.</summary>
    DecreaseReceivable,
}

/// <summary>An imported acquirer statement (REC-1). Imported once: the file hash is unique.</summary>
internal sealed class Statement
{
    public Guid Id { get; init; }

    public required string SourceName { get; init; }

    public DateOnly PeriodStart { get; init; }

    /// <summary>Inclusive.</summary>
    public DateOnly PeriodEnd { get; init; }

    public required string FileName { get; init; }

    public required byte[] FileSha256 { get; init; }

    public int LineCount { get; init; }

    public required string ImportedBy { get; init; }

    public DateTimeOffset ImportedAt { get; init; }

    public StatementStatus Status { get; set; }

    public Guid? LastRunId { get; set; }

    public DateTimeOffset PeriodStartUtc => new(PeriodStart.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);

    public DateTimeOffset PeriodEndExclusiveUtc => new(PeriodEnd.AddDays(1).ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
}

/// <summary>One statement line, exactly as imported (append-only).</summary>
internal sealed class StatementLine
{
    public long Id { get; init; }

    public Guid StatementId { get; init; }

    public int LineNumber { get; init; }

    public required string ExternalId { get; init; }

    public required string Reference { get; init; }

    public StatementTransactionType TransactionType { get; init; }

    public decimal Amount { get; init; }

    public required string Currency { get; init; }

    public required string ExternalStatus { get; init; }

    public DateOnly ValueDate { get; init; }

    public required string Raw { get; init; }
}

internal sealed class ReconciliationRun
{
    public Guid Id { get; init; }

    public Guid StatementId { get; init; }

    public DateTimeOffset StartedAt { get; init; }

    public DateTimeOffset CompletedAt { get; set; }

    public required string StartedBy { get; init; }

    public int Matched { get; set; }

    public int MissingExternal { get; set; }

    public int MissingInternal { get; set; }

    public int AmountMismatch { get; set; }

    public int CurrencyMismatch { get; set; }

    public int DuplicateExternal { get; set; }

    public int StatusDiscrepancy { get; set; }

    public int ExceptionsCreated { get; set; }
}

/// <summary>One classified line or internal record from a run (append-only).</summary>
internal sealed class ReconciliationResult
{
    public long Id { get; init; }

    public Guid RunId { get; init; }

    public ReconciliationOutcome Outcome { get; init; }

    public long? StatementLineId { get; init; }

    public int? LineNumber { get; init; }

    public string? InternalType { get; init; }

    public Guid? InternalId { get; init; }

    public string? Reference { get; init; }

    public string? ExternalId { get; init; }

    public decimal? InternalAmount { get; init; }

    public decimal? ExternalAmount { get; init; }

    public string? InternalCurrency { get; init; }

    public string? ExternalCurrency { get; init; }

    public string? InternalStatus { get; init; }

    public string? ExternalStatus { get; init; }

    public required string Detail { get; init; }
}

/// <summary>
/// A discrepancy to investigate (REC-4). The fingerprint makes reruns of a statement update the
/// same exception instead of creating another (REC-5).
/// Open → InReview → Resolved | Dismissed (Open can be resolved or dismissed directly).
/// </summary>
internal sealed class ReconciliationException
{
    private ReconciliationException()
    {
    }

    public Guid Id { get; private init; }

    public byte[] Fingerprint { get; private init; } = [];

    public ReconciliationOutcome Outcome { get; private init; }

    public Guid StatementId { get; private init; }

    public long LatestResultId { get; private set; }

    public string? Reference { get; private init; }

    public string? ExternalId { get; private init; }

    public string? InternalType { get; private init; }

    public Guid? InternalId { get; private init; }

    public decimal? InternalAmount { get; private set; }

    public decimal? ExternalAmount { get; private set; }

    public string? InternalCurrency { get; private init; }

    public string? ExternalCurrency { get; private init; }

    /// <summary>Currency an adjustment is posted in: the statement's, or PayFlow's when the line is missing.</summary>
    public string Currency => ExternalCurrency ?? InternalCurrency!;

    public string Detail { get; private set; } = string.Empty;

    public ExceptionStatus Status { get; private set; }

    public string? AssignedTo { get; private set; }

    public ResolutionType? ResolutionType { get; private set; }

    public string? ResolutionNote { get; private set; }

    public decimal? AdjustmentAmount { get; private set; }

    public AdjustmentDirection? AdjustmentDirection { get; private set; }

    public string? ResolvedBy { get; private set; }

    public DateTimeOffset? ResolvedAt { get; private set; }

    public DateTimeOffset CreatedAt { get; private init; }

    public DateTimeOffset UpdatedAt { get; private set; }

    public int TimesSeen { get; private set; }

    public uint Version { get; private set; }

    public static ReconciliationException Raise(Guid id, byte[] fingerprint, Guid statementId, ReconciliationResult result, DateTimeOffset now) => new()
    {
        Id = id,
        Fingerprint = fingerprint,
        Outcome = result.Outcome,
        StatementId = statementId,
        LatestResultId = result.Id,
        Reference = result.Reference,
        ExternalId = result.ExternalId,
        InternalType = result.InternalType,
        InternalId = result.InternalId,
        InternalAmount = result.InternalAmount,
        ExternalAmount = result.ExternalAmount,
        InternalCurrency = result.InternalCurrency,
        ExternalCurrency = result.ExternalCurrency,
        Detail = result.Detail,
        Status = ExceptionStatus.Open,
        CreatedAt = now,
        UpdatedAt = now,
        TimesSeen = 1,
    };

    /// <summary>A rerun found the same discrepancy again.</summary>
    public void SeenAgain(ReconciliationResult result, DateTimeOffset now)
    {
        LatestResultId = result.Id;
        InternalAmount = result.InternalAmount;
        ExternalAmount = result.ExternalAmount;
        Detail = result.Detail;
        TimesSeen++;
        UpdatedAt = now;
    }

    public Result StartReview(string actor, DateTimeOffset now)
    {
        if (Status != ExceptionStatus.Open)
        {
            return Invalid(ExceptionStatus.InReview);
        }

        Status = ExceptionStatus.InReview;
        AssignedTo = actor;
        UpdatedAt = now;
        return Result.Success();
    }

    public Result Resolve(ResolutionType type, string note, string actor, DateTimeOffset now)
    {
        if (type is Domain.ResolutionType.AdjustmentPosted or Domain.ResolutionType.FalsePositive)
        {
            return Error.Validation("invalid_resolution_type", "Use the adjustment field to post an adjustment, and dismiss for false positives.");
        }

        return Close(ExceptionStatus.Resolved, type, note, actor, now);
    }

    /// <summary>Resolves with a ledger adjustment; the caller posts the journal entry in the same transaction.</summary>
    public Result ResolveWithAdjustment(decimal amount, AdjustmentDirection direction, string note, string actor, DateTimeOffset now)
    {
        if (amount <= 0)
        {
            return Error.Validation("invalid_adjustment_amount", "The adjustment amount must be positive.");
        }

        var closed = Close(ExceptionStatus.Resolved, Domain.ResolutionType.AdjustmentPosted, note, actor, now);
        if (closed.IsSuccess)
        {
            AdjustmentAmount = amount;
            AdjustmentDirection = direction;
        }

        return closed;
    }

    public Result Dismiss(string note, string actor, DateTimeOffset now) => Close(ExceptionStatus.Dismissed, Domain.ResolutionType.FalsePositive, note, actor, now);

    private Result Close(ExceptionStatus target, ResolutionType type, string note, string actor, DateTimeOffset now)
    {
        if (Status is not (ExceptionStatus.Open or ExceptionStatus.InReview))
        {
            return Invalid(target);
        }

        if (string.IsNullOrWhiteSpace(note))
        {
            return Error.Validation("note_required", "A note explaining the resolution is required.");
        }

        Status = target;
        ResolutionType = type;
        ResolutionNote = note.Trim();
        ResolvedBy = actor;
        ResolvedAt = now;
        AssignedTo ??= actor;
        UpdatedAt = now;
        return Result.Success();
    }

    private Error Invalid(ExceptionStatus target) =>
        Error.Unprocessable("invalid_exception_transition", $"An exception cannot move from {Status} to {target}.");
}

/// <summary>Status changes and notes (append-only).</summary>
internal sealed class ExceptionHistoryEntry
{
    public long Id { get; init; }

    public Guid ExceptionId { get; init; }

    public ExceptionStatus? FromStatus { get; init; }

    public ExceptionStatus ToStatus { get; init; }

    public string? Note { get; init; }

    public required string Actor { get; init; }

    public DateTimeOffset OccurredAt { get; init; }
}
