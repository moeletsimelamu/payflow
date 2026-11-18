using FluentValidation;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using PayFlow.Infrastructure.Http;
using PayFlow.Modules.Identity.Contracts;
using PayFlow.Modules.Reconciliation.Application;
using PayFlow.Modules.Reconciliation.Domain;
using PayFlow.SharedKernel.Ids;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Modules.Reconciliation.Endpoints;

internal sealed class ImportStatementRequestValidator : AbstractValidator<ImportStatementRequest>
{
    public const int MaxCsvLength = 5_000_000;

    public ImportStatementRequestValidator()
    {
        RuleFor(r => r.SourceName).NotEmpty().MaximumLength(100);
        RuleFor(r => r.FileName).NotEmpty().MaximumLength(200);
        RuleFor(r => r.Csv).NotEmpty().MaximumLength(MaxCsvLength);
        RuleFor(r => r.PeriodEnd).GreaterThanOrEqualTo(r => r.PeriodStart);
    }
}

internal sealed class GenerateStatementRequestValidator : AbstractValidator<GenerateStatementRequest>
{
    public GenerateStatementRequestValidator()
    {
        RuleFor(r => r.PeriodEnd).GreaterThanOrEqualTo(r => r.PeriodStart);
        RuleFor(r => r).Must(r => r.PeriodEnd.DayNumber - r.PeriodStart.DayNumber < 31).WithName("period").WithMessage("A statement covers at most 31 days.");
        RuleFor(r => r.SourceName).MaximumLength(100);
        When(r => r.Inject is not null, () =>
        {
            RuleFor(r => r.Inject!.MissingExternal).InclusiveBetween(0, StatementGenerator.MaxPerKind);
            RuleFor(r => r.Inject!.MissingInternal).InclusiveBetween(0, StatementGenerator.MaxPerKind);
            RuleFor(r => r.Inject!.AmountMismatch).InclusiveBetween(0, StatementGenerator.MaxPerKind);
            RuleFor(r => r.Inject!.CurrencyMismatch).InclusiveBetween(0, StatementGenerator.MaxPerKind);
            RuleFor(r => r.Inject!.DuplicateExternal).InclusiveBetween(0, StatementGenerator.MaxPerKind);
            RuleFor(r => r.Inject!.StatusDiscrepancy).InclusiveBetween(0, StatementGenerator.MaxPerKind);
        });
    }
}

internal sealed class ResolveExceptionRequestValidator : AbstractValidator<ResolveExceptionRequest>
{
    public ResolveExceptionRequestValidator()
    {
        RuleFor(r => r.Note).NotEmpty().MaximumLength(1000);
        RuleFor(r => r.ResolutionType).IsInEnum().When(r => r.ResolutionType is not null);
        When(r => r.Adjustment is not null, () =>
        {
            RuleFor(r => r.Adjustment!.Amount).NotEmpty().MaximumLength(32);
            RuleFor(r => r.Adjustment!.Direction).IsInEnum();
        });
    }
}

internal sealed class ExceptionNoteRequestValidator : AbstractValidator<ExceptionNoteRequest>
{
    public ExceptionNoteRequestValidator() => RuleFor(r => r.Note).MaximumLength(1000);
}

/// <summary>Reconciliation is a platform-operations workspace: every endpoint is PlatformAdmin.</summary>
internal static class ReconciliationEndpoints
{
    public static void Map(IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/reconciliation").WithTags("Reconciliation").RequireAuthorization(AuthPolicies.PlatformAdmin);

        group.MapPost("/statements", async (ImportStatementRequest request, ReconciliationService service, CancellationToken ct) =>
                (await service.ImportAsync(request, ct)).ToHttp(s => Results.Created($"/api/reconciliation/statements/{s.Id}", s)))
            .WithValidation<ImportStatementRequest>();

        group.MapGet("/statements", async (int? page, int? pageSize, ReconciliationService service, CancellationToken ct) =>
            Results.Ok(await service.ListStatementsAsync(page ?? 1, pageSize ?? 25, ct)));

        group.MapPost("/statements/generate", async (GenerateStatementRequest request, StatementGenerator generator, CancellationToken ct) =>
                (await generator.GenerateAsync(request, ct)).ToHttp())
            .WithValidation<GenerateStatementRequest>();

        group.MapGet("/statements/{statementId}", (string statementId, ReconciliationService service, CancellationToken ct) =>
            With(statementId, IdPrefixes.Statement, "statement_not_found", async id => (await service.GetStatementAsync(id, ct)).ToHttp()));

        group.MapPost("/statements/{statementId}/runs", (string statementId, ReconciliationService service, CancellationToken ct) =>
            With(statementId, IdPrefixes.Statement, "statement_not_found", async id => (await service.RunAsync(id, ct))
                .ToHttp(r => Results.Created($"/api/reconciliation/runs/{r.Id}", r))));

        group.MapGet("/runs/{runId}", (string runId, ReconciliationService service, CancellationToken ct) =>
            With(runId, IdPrefixes.ReconciliationRun, "reconciliation_run_not_found", async id => (await service.GetRunAsync(id, ct)).ToHttp()));

        group.MapGet("/runs/{runId}/results", (string runId, ReconciliationOutcome? outcome, int? page, int? pageSize, ReconciliationService service, CancellationToken ct) =>
            With(runId, IdPrefixes.ReconciliationRun, "reconciliation_run_not_found", async id =>
                (await service.ListResultsAsync(id, outcome, page ?? 1, pageSize ?? 50, ct)).ToHttp()));

        group.MapGet("/exceptions", async (ExceptionStatus? status, ReconciliationOutcome? outcome, string? statementId, int? page, int? pageSize, ReconciliationService service, CancellationToken ct) =>
        {
            Guid? statement = null;
            if (statementId is not null)
            {
                if (!PublicId.TryParse(statementId, IdPrefixes.Statement, out var s))
                {
                    return ErrorResults.Problem(Error.Validation("invalid_statement_id", "statementId must be a stmt_... id."));
                }

                statement = s;
            }

            return Results.Ok(await service.ListExceptionsAsync(status, outcome, statement, page ?? 1, pageSize ?? 25, ct));
        });

        group.MapGet("/exceptions/{exceptionId}", (string exceptionId, ReconciliationService service, CancellationToken ct) =>
            WithException(exceptionId, async id => (await service.GetExceptionAsync(id, ct)).ToHttp()));

        group.MapPost("/exceptions/{exceptionId}/review", (string exceptionId, ExceptionNoteRequest request, ReconciliationService service, CancellationToken ct) =>
                WithException(exceptionId, async id => (await service.StartReviewAsync(id, request, ct)).ToHttp()))
            .WithValidation<ExceptionNoteRequest>();

        group.MapPost("/exceptions/{exceptionId}/resolve", (string exceptionId, ResolveExceptionRequest request, ReconciliationService service, CancellationToken ct) =>
                WithException(exceptionId, async id => (await service.ResolveAsync(id, request, ct)).ToHttp()))
            .WithValidation<ResolveExceptionRequest>();

        group.MapPost("/exceptions/{exceptionId}/dismiss", (string exceptionId, ExceptionNoteRequest request, ReconciliationService service, CancellationToken ct) =>
                WithException(exceptionId, async id => (await service.DismissAsync(id, request, ct)).ToHttp()))
            .WithValidation<ExceptionNoteRequest>();
    }

    private static Task<IResult> WithException(string exceptionId, Func<Guid, Task<IResult>> handler) =>
        With(exceptionId, IdPrefixes.ReconciliationException, "reconciliation_exception_not_found", handler);

    private static async Task<IResult> With(string value, string prefix, string notFoundCode, Func<Guid, Task<IResult>> handler) =>
        PublicId.TryParse(value, prefix, out var id)
            ? await handler(id.Value)
            : ErrorResults.Problem(Error.NotFound(notFoundCode, "Not found."));
}
