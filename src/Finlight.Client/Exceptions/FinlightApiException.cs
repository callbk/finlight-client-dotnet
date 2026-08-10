namespace Finlight;

/// <summary>
/// Thrown for non-2xx REST responses, after retryable statuses (429, 500, 502,
/// 503, 504) have exhausted their retry budget.
/// </summary>
public sealed class FinlightApiException : FinlightException
{
    /// <summary>Initializes the exception from a failed HTTP response.</summary>
    public FinlightApiException(int statusCode, string? reasonPhrase, string body)
        : base($"finlight: API error: {statusCode} {reasonPhrase}".TrimEnd())
    {
        StatusCode = statusCode;
        ReasonPhrase = reasonPhrase;
        Body = body;
    }

    /// <summary>The HTTP status code of the failed response.</summary>
    public int StatusCode { get; }

    /// <summary>The HTTP reason phrase, when the server sent one.</summary>
    public string? ReasonPhrase { get; }

    /// <summary>The raw response body.</summary>
    public string Body { get; }
}
