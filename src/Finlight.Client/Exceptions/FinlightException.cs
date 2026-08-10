namespace Finlight;

/// <summary>Base type for all exceptions thrown by the finlight client.</summary>
public abstract class FinlightException : Exception
{
    /// <summary>Initializes the exception with a message.</summary>
    protected FinlightException(string message)
        : base(message)
    {
    }

    /// <summary>Initializes the exception with a message and an inner exception.</summary>
    protected FinlightException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }
}
