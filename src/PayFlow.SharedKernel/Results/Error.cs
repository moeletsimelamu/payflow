namespace PayFlow.SharedKernel.Results;

/// <summary>Category of failure; maps one-to-one onto an HTTP status at the API edge.</summary>
public enum ErrorKind
{
    Validation,
    Unauthorized,
    Forbidden,
    NotFound,
    Conflict,
    Unprocessable,
    TooManyRequests,
}

/// <summary>
/// A business or validation failure with a stable machine-readable <see cref="Code"/>
/// (snake_case, part of the public API contract) and a human-readable message.
/// </summary>
public sealed record Error(string Code, string Message, ErrorKind Kind)
{
    public IReadOnlyDictionary<string, string[]>? Details { get; init; }

    public static Error Validation(string code, string message, IReadOnlyDictionary<string, string[]>? details = null) =>
        new(code, message, ErrorKind.Validation) { Details = details };

    public static Error NotFound(string code, string message) => new(code, message, ErrorKind.NotFound);

    public static Error Conflict(string code, string message) => new(code, message, ErrorKind.Conflict);

    public static Error Unprocessable(string code, string message) => new(code, message, ErrorKind.Unprocessable);

    public static Error Unauthorized(string code, string message) => new(code, message, ErrorKind.Unauthorized);

    public static Error Forbidden(string code, string message) => new(code, message, ErrorKind.Forbidden);
}
