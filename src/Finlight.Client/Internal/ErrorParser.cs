using System.Globalization;
using System.Text.Json;

namespace Finlight.Internal;

/// <summary>Turns an error response into a <see cref="FinlightApiException"/> (R-ERR-1).</summary>
internal static class ErrorParser
{
    private const string MonthlyMarker = "Exceeded token limit";
    private const string BurstMarker = "Exceeded rate limit";

    /// <summary>
    /// Reads the body in this order: problem+json, the NestJS form <c>{statusCode, message}</c>,
    /// then a body that is not JSON (status plus text).
    /// </summary>
    public static FinlightApiException Parse(
        int status,
        string? contentType,
        string text,
        string? sentRequestId,
        TimeSpan? retryAfter)
    {
        JsonElement? parsed = null;
        if (text.Length > 0)
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                parsed = doc.RootElement.Clone();
            }
            catch (JsonException)
            {
                parsed = null;
            }
        }

        if (parsed is { ValueKind: JsonValueKind.Object } obj)
        {
            var isProblem = (contentType ?? string.Empty).Contains("application/problem+json", StringComparison.OrdinalIgnoreCase)
                || (Str(obj, "code") is not null && (Str(obj, "type") is not null || Str(obj, "title") is not null));
            if (isProblem)
            {
                return FinlightApiException.Create(
                    new ApiErrorData
                    {
                        Status = status,
                        Code = Str(obj, "code"),
                        Type = Str(obj, "type"),
                        Title = Str(obj, "title"),
                        Detail = Str(obj, "detail"),
                        RequestId = Str(obj, "requestId") ?? sentRequestId,
                        Errors = Issues(obj),
                        Raw = text,
                        LimitType = status == 429 ? ClassifyRateLimit(obj) : (RateLimitType?)null,
                        RetryAfter = retryAfter,
                    },
                    kindFromStatus: false);
            }

            // NestJS default form: {statusCode, message: string | string[], error}
            var messages = new List<string>();
            var isArray = false;
            if (obj.TryGetProperty("message", out var message))
            {
                if (message.ValueKind == JsonValueKind.String)
                {
                    messages.Add(message.GetString()!);
                }
                else if (message.ValueKind == JsonValueKind.Array)
                {
                    isArray = true;
                    messages.AddRange(message.EnumerateArray()
                        .Where(m => m.ValueKind == JsonValueKind.String)
                        .Select(m => m.GetString()!));
                }
            }

            return FinlightApiException.Create(
                new ApiErrorData
                {
                    Status = status,
                    Title = Str(obj, "error"),
                    Detail = messages.Count > 0 ? string.Join("; ", messages) : null,
                    RequestId = Str(obj, "requestId") ?? sentRequestId,
                    Errors = isArray ? messages.Select(m => new FieldIssue { Issue = m }).ToList() : [],
                    Raw = text,
                    LimitType = status == 429 ? ClassifyRateLimit(obj) : (RateLimitType?)null,
                    RetryAfter = retryAfter,
                },
                kindFromStatus: true);
        }

        return FinlightApiException.Create(
            new ApiErrorData
            {
                Status = status,
                RequestId = sentRequestId,
                Raw = text.Length > 0 ? text : null,
                LimitType = status == 429 ? RateLimitType.Unknown : (RateLimitType?)null,
                RetryAfter = retryAfter,
                Message = $"finlight API error {status}: the server answered with a body that is not JSON"
                    + (sentRequestId is null ? string.Empty : $" (requestId {sentRequestId})"),
            },
            kindFromStatus: status < 500);
    }

    /// <summary>
    /// Classifies a 429 in exactly one place: the <c>limitType</c> member first (kept as sent, also
    /// when unknown), then the exact marker text in <c>detail</c> (or <c>message</c>).
    /// </summary>
    public static RateLimitType ClassifyRateLimit(JsonElement body)
    {
        if (Str(body, "limitType") is { Length: > 0 } limitType)
        {
            return new RateLimitType(limitType);
        }

        var detail = Str(body, "detail") ?? Str(body, "message") ?? string.Empty;
        if (detail.Contains(MonthlyMarker, StringComparison.Ordinal))
        {
            return RateLimitType.Monthly;
        }

        return detail.Contains(BurstMarker, StringComparison.Ordinal) ? RateLimitType.Burst : RateLimitType.Unknown;
    }

    /// <summary>Parses <c>Retry-After</c>: delta seconds or an HTTP date.</summary>
    public static TimeSpan? ParseRetryAfter(string? value, DateTimeOffset now)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.Length > 0 && trimmed.All(char.IsAsciiDigit))
        {
            // Server input: a huge number is cut to the timer limit instead of overflowing.
            return decimal.TryParse(trimmed, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
                && seconds < (decimal)Limits.MaxWait.TotalSeconds
                ? TimeSpan.FromSeconds((double)seconds)
                : Limits.MaxWait;
        }

        if (DateTimeOffset.TryParseExact(trimmed, "r", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date))
        {
            var wait = date - now;
            return wait <= TimeSpan.Zero ? TimeSpan.Zero : wait < Limits.MaxWait ? wait : Limits.MaxWait;
        }

        return null;
    }

    /// <summary>The retry class of an API error (R-RETRY-1).</summary>
    public static RetryClass Classify(FinlightApiException error)
    {
        if (error is RateLimitedException rateLimited)
        {
            return rateLimited.LimitType == RateLimitType.Burst ? RetryClass.Burst : RetryClass.Never;
        }

        if (error.Status == 429)
        {
            return RetryClass.Never;
        }

        return error.Status >= 500 ? RetryClass.Backoff : RetryClass.Never;
    }

    public static string? Str(JsonElement obj, string key)
        => obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static List<FieldIssue> Issues(JsonElement obj)
    {
        var list = new List<FieldIssue>();
        if (!obj.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array)
        {
            return list;
        }

        foreach (var entry in errors.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            List<string>? allowed = null;
            if (entry.TryGetProperty("allowedValues", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                allowed = values.EnumerateArray()
                    .Where(v => v.ValueKind == JsonValueKind.String)
                    .Select(v => v.GetString()!)
                    .ToList();
            }

            list.Add(new FieldIssue
            {
                Field = Str(entry, "field"),
                Issue = Str(entry, "issue"),
                AllowedValues = allowed,
            });
        }

        return list;
    }
}

/// <summary>What the retry loop does with an error.</summary>
internal enum RetryClass
{
    Never,
    Burst,
    Backoff,
}
