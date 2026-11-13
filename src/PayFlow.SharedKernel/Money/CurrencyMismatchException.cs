namespace PayFlow.SharedKernel.Money;

/// <summary>Thrown when arithmetic or comparison is attempted between amounts in different currencies.</summary>
public sealed class CurrencyMismatchException : InvalidOperationException
{
    public CurrencyMismatchException()
    {
    }

    public CurrencyMismatchException(string message)
        : base(message)
    {
    }

    public CurrencyMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
    }

    public CurrencyMismatchException(Currency left, Currency right)
        : base($"Cannot combine amounts in {left?.Code} and {right?.Code}.")
    {
    }
}
