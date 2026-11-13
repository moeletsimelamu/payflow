namespace PayFlow.SharedKernel.Results;

/// <summary>
/// Thrown when a domain invariant is violated by a programming error, such as code requesting an
/// illegal state transition. Expected business failures use <see cref="Result"/> instead.
/// </summary>
public class DomainException : Exception
{
    public DomainException()
        : this(new Error("domain_error", "A domain rule was violated.", ErrorKind.Unprocessable))
    {
    }

    public DomainException(string message)
        : this(new Error("domain_error", message, ErrorKind.Unprocessable))
    {
    }

    public DomainException(string message, Exception innerException)
        : base(message, innerException) => Error = new Error("domain_error", message, ErrorKind.Unprocessable);

    public DomainException(Error error)
        : base(error?.Message) => Error = error ?? throw new ArgumentNullException(nameof(error));

    public Error Error { get; }
}
