using System.Text.Json;

namespace Finlight;

/// <summary>
/// The base of every error this SDK throws. <see cref="Kind"/> tells the kind as text, without
/// type checks. Cancellation by the caller is never a <see cref="FinlightException"/>: it arrives as
/// <see cref="OperationCanceledException"/>.
/// </summary>
public abstract class FinlightException : Exception
{
    /// <summary>Creates the error.</summary>
    /// <param name="message">The message (never contains the API key, secret or a full body).</param>
    /// <param name="innerException">The original error, if any.</param>
    private protected FinlightException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <summary>The kind of this error: <c>api</c>, <c>validation</c>, <c>network</c>, <c>timeout</c>,
    /// <c>decode</c>, <c>webhook_verification</c> or <c>preempted</c>.</summary>
    public abstract FinlightErrorKind Kind { get; }
}

/// <summary>One entry of <see cref="FinlightApiException.Errors"/>.</summary>
public sealed record FieldIssue
{
    /// <summary>The request field, e.g. <c>eventTypes</c>; null when the server named none.</summary>
    public string? Field { get; init; }

    /// <summary>What is wrong with it.</summary>
    public string? Issue { get; init; }

    /// <summary>The accepted values, for closed request vocabularies (REST errors and stream <c>connection.error</c> details).</summary>
    public IReadOnlyList<string>? AllowedValues { get; init; }

    /// <summary>Rule name to text; only on stream validation errors (<c>connection.error</c> details).</summary>
    public IReadOnlyDictionary<string, string>? Constraints { get; init; }
}

/// <summary>
/// An error the API answered: a REST error response, a refused WebSocket connect, or a terminal
/// <c>connection.error</c>. Known codes have a subclass each; an unknown code is this type with
/// <see cref="ApiKind"/> <c>other</c>.
/// </summary>
public class FinlightApiException : FinlightException
{
    internal FinlightApiException(ApiErrorData data, ApiErrorKind apiKind)
        : base(BuildMessage(data), data.Cause)
    {
        Status = data.Status;
        Code = data.Code;
        Type = data.Type;
        Title = data.Title;
        Detail = data.Detail;
        RequestId = data.RequestId;
        Errors = data.Errors ?? [];
        Raw = data.Raw;
        ApiKind = apiKind;
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.Api;

    /// <summary>The kind derived from <see cref="Code"/> or, without a code, from the status.</summary>
    public ApiErrorKind ApiKind { get; }

    /// <summary>The HTTP status (0 for a <c>connection.error</c> frame).</summary>
    public int Status { get; }

    /// <summary>The problem code (e.g. <c>NOT_FOUND</c>) or the <c>connection.error</c> code; null without one.
    /// Compare with <see cref="ProblemCodeValues"/> and <see cref="ConnectionErrorCodeValues"/>.</summary>
    public string? Code { get; }

    /// <summary>The problem <c>type</c> URI.</summary>
    public string? Type { get; }

    /// <summary>The problem <c>title</c>.</summary>
    public string? Title { get; }

    /// <summary>The problem <c>detail</c> (or the NestJS <c>message</c>).</summary>
    public string? Detail { get; }

    /// <summary>The request id: the server's, else the <c>X-Request-Id</c> this SDK sent.</summary>
    public string? RequestId { get; }

    /// <summary>Field errors of validation and cursor errors; empty otherwise.</summary>
    public IReadOnlyList<FieldIssue> Errors { get; }

    /// <summary>The error body as received (text), or null when there was none.</summary>
    public string? Raw { get; }

    /// <summary>The error body as JSON text, or null when the body was not JSON.</summary>
    /// <returns>The JSON text or null.</returns>
    public string? GetRawJson()
    {
        if (Raw is null)
        {
            return null;
        }

        try
        {
            using var _ = JsonDocument.Parse(Raw);
            return Raw;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string BuildMessage(ApiErrorData data)
    {
        if (data.Message is { } message)
        {
            return message;
        }

        var text = $"finlight API error {data.Status}";
        if (data.Code is not null)
        {
            text += $" {data.Code}";
        }

        if (!string.IsNullOrEmpty(data.Detail))
        {
            text += $": {data.Detail}";
        }

        if (data.RequestId is not null)
        {
            text += $" (requestId {data.RequestId})";
        }

        return text;
    }

    /// <summary>Creates the subclass that matches the code, or the status when there is no code.</summary>
    internal static FinlightApiException Create(ApiErrorData data, bool kindFromStatus)
    {
        var kind = data.Code switch
        {
            ProblemCodeValues.ValidationFailed => ApiErrorKind.ValidationFailed,
            ProblemCodeValues.CursorInvalid => ApiErrorKind.CursorInvalid,
            ProblemCodeValues.CursorMismatch => ApiErrorKind.CursorMismatch,
            ProblemCodeValues.Unauthorized => ApiErrorKind.Unauthorized,
            ProblemCodeValues.Forbidden => ApiErrorKind.Forbidden,
            ProblemCodeValues.NotFound => ApiErrorKind.NotFound,
            ProblemCodeValues.RateLimited => ApiErrorKind.RateLimited,
            ProblemCodeValues.InternalError => ApiErrorKind.InternalError,
            null when kindFromStatus => KindFromStatus(data.Status),
            _ => ApiErrorKind.Other,
        };

        return kind.Value switch
        {
            ApiErrorKindValues.ValidationFailed => new ValidationFailedException(data),
            ApiErrorKindValues.CursorInvalid => new CursorInvalidException(data),
            ApiErrorKindValues.CursorMismatch => new CursorMismatchException(data),
            ApiErrorKindValues.Unauthorized => new UnauthorizedException(data),
            ApiErrorKindValues.Forbidden => new ForbiddenException(data),
            ApiErrorKindValues.NotFound => new NotFoundException(data),
            ApiErrorKindValues.RateLimited => new RateLimitedException(data),
            ApiErrorKindValues.InternalError => new InternalServerException(data),
            _ => new FinlightApiException(data, ApiErrorKind.Other),
        };
    }

    private static ApiErrorKind KindFromStatus(int status) => status switch
    {
        400 => ApiErrorKind.ValidationFailed,
        401 => ApiErrorKind.Unauthorized,
        403 => ApiErrorKind.Forbidden,
        404 => ApiErrorKind.NotFound,
        429 => ApiErrorKind.RateLimited,
        >= 500 and <= 599 => ApiErrorKind.InternalError,
        _ => ApiErrorKind.Other,
    };
}

/// <summary><c>VALIDATION_FAILED</c> (400): a request field is not valid.</summary>
public sealed class ValidationFailedException : FinlightApiException
{
    internal ValidationFailedException(ApiErrorData data)
        : base(data, ApiErrorKind.ValidationFailed)
    {
    }
}

/// <summary><c>CURSOR_INVALID</c> (400): the cursor cannot be read.</summary>
public sealed class CursorInvalidException : FinlightApiException
{
    internal CursorInvalidException(ApiErrorData data)
        : base(data, ApiErrorKind.CursorInvalid)
    {
    }
}

/// <summary><c>CURSOR_MISMATCH</c> (400): sort order or filters changed since the cursor was issued.</summary>
public sealed class CursorMismatchException : FinlightApiException
{
    internal CursorMismatchException(ApiErrorData data)
        : base(data, ApiErrorKind.CursorMismatch)
    {
    }
}

/// <summary><c>UNAUTHORIZED</c> (401): missing or invalid API key.</summary>
public sealed class UnauthorizedException : FinlightApiException
{
    internal UnauthorizedException(ApiErrorData data)
        : base(data, ApiErrorKind.Unauthorized)
    {
    }
}

/// <summary><c>FORBIDDEN</c> (403): the plan does not include this (e.g. <c>entityAccess</c>, WebSockets).</summary>
public sealed class ForbiddenException : FinlightApiException
{
    internal ForbiddenException(ApiErrorData data)
        : base(data, ApiErrorKind.Forbidden)
    {
    }
}

/// <summary><c>NOT_FOUND</c> (404): the lookup found nothing.</summary>
public sealed class NotFoundException : FinlightApiException
{
    internal NotFoundException(ApiErrorData data)
        : base(data, ApiErrorKind.NotFound)
    {
    }
}

/// <summary><c>RATE_LIMITED</c> (429): the monthly quota or the burst limit is used up.</summary>
public sealed class RateLimitedException : FinlightApiException
{
    internal RateLimitedException(ApiErrorData data)
        : base(data, ApiErrorKind.RateLimited)
    {
        LimitType = data.LimitType ?? RateLimitType.Unknown;
        RetryAfter = data.RetryAfter;
    }

    /// <summary><c>monthly</c>, <c>burst</c>, <c>unknown</c> or the value the server sent.</summary>
    public RateLimitType LimitType { get; }

    /// <summary>The <c>Retry-After</c> header (burst only), or null.</summary>
    public TimeSpan? RetryAfter { get; }
}

/// <summary><c>INTERNAL_ERROR</c> (5xx): the server could not process the request.</summary>
public sealed class InternalServerException : FinlightApiException
{
    internal InternalServerException(ApiErrorData data)
        : base(data, ApiErrorKind.InternalError)
    {
    }
}

/// <summary>A local check failed before any request or connect (R-REQ-3).</summary>
public sealed class FinlightValidationException : FinlightException
{
    internal FinlightValidationException(string message, string? param = null)
        : base(message)
    {
        Param = param;
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.Validation;

    /// <summary>The rejected parameter, or null.</summary>
    public string? Param { get; }
}

/// <summary>The request did not reach the API or no response came back.</summary>
public class FinlightNetworkException : FinlightException
{
    internal FinlightNetworkException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.Network;
}

/// <summary>An attempt or the whole call ran out of time.</summary>
public sealed class FinlightTimeoutException : FinlightNetworkException
{
    internal FinlightTimeoutException(string message, Exception? innerException = null)
        : base(message, innerException)
    {
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.Timeout;
}

/// <summary>A response could not be read into the models (R-MODELS-1).</summary>
public sealed class FinlightDecodeException : FinlightException
{
    internal FinlightDecodeException(string message, string? path = null, string? raw = null, Exception? innerException = null)
        : base(message, innerException)
    {
        Path = path;
        Raw = raw;
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.Decode;

    /// <summary>The JSON path of the value that did not fit, or null.</summary>
    public string? Path { get; }

    /// <summary>The JSON text that did not fit, or null.</summary>
    public string? Raw { get; }
}

/// <summary>A webhook failed verification (R-HOOK-1). Answer it with 400, never with 410.</summary>
public sealed class WebhookVerificationException : FinlightException
{
    internal WebhookVerificationException(WebhookVerificationReason reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.WebhookVerification;

    /// <summary>Why the verification failed.</summary>
    public WebhookVerificationReason Reason { get; }
}

/// <summary>The server ended the stream with <c>connection.preempted</c>.</summary>
public sealed class PreemptedException : FinlightException
{
    internal PreemptedException(PreemptReason reason, string? newLeaseId, long? timestamp)
        : base($"the stream was preempted by the server (reason {reason.Value})")
    {
        Reason = reason;
        NewLeaseId = newLeaseId;
        Timestamp = timestamp;
    }

    /// <inheritdoc />
    public override FinlightErrorKind Kind => FinlightErrorKind.Preempted;

    /// <summary><c>connection_limit</c> (another connection took over) or <c>quota_exceeded</c>.</summary>
    public PreemptReason Reason { get; }

    /// <summary>The lease of the connection that took over, or null.</summary>
    public string? NewLeaseId { get; }

    /// <summary>When it happened, epoch milliseconds, or null.</summary>
    public long? Timestamp { get; }
}

/// <summary>Everything an API error carries; filled by the parsers.</summary>
internal sealed record ApiErrorData
{
    public int Status { get; init; }

    public string? Code { get; init; }

    public string? Type { get; init; }

    public string? Title { get; init; }

    public string? Detail { get; init; }

    public string? RequestId { get; init; }

    public IReadOnlyList<FieldIssue>? Errors { get; init; }

    public string? Raw { get; init; }

    public RateLimitType? LimitType { get; init; }

    public TimeSpan? RetryAfter { get; init; }

    public string? Message { get; init; }

    public Exception? Cause { get; init; }
}
