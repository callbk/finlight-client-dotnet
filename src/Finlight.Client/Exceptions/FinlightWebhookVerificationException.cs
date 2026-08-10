namespace Finlight;

/// <summary>
/// Thrown by <see cref="FinlightWebhooks.ConstructEvent(ReadOnlySpan{byte}, string, string, string?)"/>
/// when a webhook fails signature, timestamp, or payload validation.
/// </summary>
public sealed class FinlightWebhookVerificationException : FinlightException
{
    /// <summary>Initializes the exception with a message.</summary>
    public FinlightWebhookVerificationException(string message)
        : base(message)
    {
    }
}
