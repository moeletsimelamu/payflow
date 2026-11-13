using Microsoft.AspNetCore.Http;
using PayFlow.SharedKernel.Results;

namespace PayFlow.Infrastructure.Http;

/// <summary>
/// Maps domain <see cref="Error"/>s to RFC 9457 problem responses. Every problem carries a stable
/// <c>code</c>; the host adds <c>correlationId</c> to all problem responses.
/// </summary>
public static class ErrorResults
{
    public static int StatusCodeFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => StatusCodes.Status400BadRequest,
        ErrorKind.Unauthorized => StatusCodes.Status401Unauthorized,
        ErrorKind.Forbidden => StatusCodes.Status403Forbidden,
        ErrorKind.NotFound => StatusCodes.Status404NotFound,
        ErrorKind.Conflict => StatusCodes.Status409Conflict,
        ErrorKind.Unprocessable => StatusCodes.Status422UnprocessableEntity,
        ErrorKind.TooManyRequests => StatusCodes.Status429TooManyRequests,
        _ => StatusCodes.Status500InternalServerError,
    };

    public static IResult Problem(Error error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var extensions = new Dictionary<string, object?> { ["code"] = error.Code };
        if (error.Details is { Count: > 0 })
        {
            extensions["errors"] = error.Details;
        }

        return Results.Problem(
            statusCode: StatusCodeFor(error.Kind),
            title: TitleFor(error.Kind),
            detail: error.Message,
            extensions: extensions);
    }

    private static string TitleFor(ErrorKind kind) => kind switch
    {
        ErrorKind.Validation => "The request is invalid.",
        ErrorKind.Unauthorized => "Authentication is required.",
        ErrorKind.Forbidden => "You do not have permission to perform this action.",
        ErrorKind.NotFound => "The resource was not found.",
        ErrorKind.Conflict => "The request conflicts with the current state.",
        ErrorKind.Unprocessable => "The request cannot be processed.",
        ErrorKind.TooManyRequests => "Too many requests.",
        _ => "An unexpected error occurred.",
    };
}
