using Microsoft.AspNetCore.Diagnostics;
using PayFlow.Infrastructure.Http;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Api.Middleware;

/// <summary>
/// Converts unhandled exceptions into problem details. Domain exceptions keep their error code;
/// anything else becomes a generic 500 that reveals nothing beyond the correlation id.
/// </summary>
public sealed class GlobalExceptionHandler(ILogger<GlobalExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext httpContext, Exception exception, CancellationToken cancellationToken)
    {
        if (exception is OperationCanceledException && httpContext.RequestAborted.IsCancellationRequested)
        {
            logger.LogInformation("Request was cancelled by the client.");
            return true;
        }

        var error = exception switch
        {
            DomainException domain => domain.Error,
            Microsoft.EntityFrameworkCore.DbUpdateConcurrencyException => Error.Conflict(
                "concurrency_conflict", "The resource was changed by another request. Reload it and try again."),
            BadHttpRequestException bad => Error.Validation("malformed_request", bad.Message),
            _ => null,
        };

        if (error is null)
        {
            logger.LogError(exception, "Unhandled exception processing {Method} {Path}.", httpContext.Request.Method, httpContext.Request.Path);
            await Results.Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "An unexpected error occurred.",
                    detail: "The error has been logged. Quote the correlation id when reporting it.",
                    extensions: new Dictionary<string, object?> { ["code"] = "internal_error" })
                .ExecuteAsync(httpContext);
            return true;
        }

        logger.LogWarning("Request failed with {ErrorCode}.", error.Code);
        await ErrorResults.Problem(error).ExecuteAsync(httpContext);
        return true;
    }
}
