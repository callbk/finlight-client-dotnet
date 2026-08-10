using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Finlight.Json;

namespace Finlight;

/// <summary>Verifies inbound finlight webhooks.</summary>
public static class FinlightWebhooks
{
    private const string SignaturePrefix = "sha256=";
    private static readonly TimeSpan ReplayTolerance = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Verifies a finlight webhook and returns the contained article.
    /// </summary>
    /// <param name="rawBody">The unmodified request body. In ASP.NET Core, enable
    /// request buffering and read the body before model binding touches it.</param>
    /// <param name="signature">The X-Webhook-Signature header value, with or
    /// without the "sha256=" prefix.</param>
    /// <param name="endpointSecret">Your webhook secret from the finlight dashboard.</param>
    /// <param name="timestamp">The X-Webhook-Timestamp header value; pass null if
    /// the webhook has none, otherwise it is included in the signed message and
    /// checked against a 5-minute replay tolerance.</param>
    /// <exception cref="FinlightWebhookVerificationException">Signature, timestamp,
    /// or payload validation failed.</exception>
    public static Article ConstructEvent(
        ReadOnlySpan<byte> rawBody,
        string signature,
        string endpointSecret,
        string? timestamp = null)
        => ConstructEvent(rawBody, signature, endpointSecret, timestamp, TimeProvider.System);

    /// <summary>Verifies a finlight webhook given the body as a string. See
    /// <see cref="ConstructEvent(ReadOnlySpan{byte}, string, string, string?)"/>.</summary>
    /// <exception cref="FinlightWebhookVerificationException">Signature, timestamp,
    /// or payload validation failed.</exception>
    public static Article ConstructEvent(
        string rawBody,
        string signature,
        string endpointSecret,
        string? timestamp = null)
        => ConstructEvent(Encoding.UTF8.GetBytes(rawBody), signature, endpointSecret, timestamp);

    internal static Article ConstructEvent(
        ReadOnlySpan<byte> rawBody,
        string signature,
        string endpointSecret,
        string? timestamp,
        TimeProvider timeProvider)
    {
        if (signature.StartsWith(SignaturePrefix, StringComparison.Ordinal))
        {
            signature = signature[SignaturePrefix.Length..];
        }

        var message = string.IsNullOrEmpty(timestamp)
            ? rawBody.ToArray()
            : [.. Encoding.UTF8.GetBytes(timestamp + "."), .. rawBody];
        var expected = Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(endpointSecret), message))
            .ToLowerInvariant();

        if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(expected), Encoding.UTF8.GetBytes(signature)))
        {
            throw new FinlightWebhookVerificationException("Invalid webhook signature");
        }

        if (!string.IsNullOrEmpty(timestamp))
        {
            VerifyTimestamp(timestamp, timeProvider);
        }

        try
        {
            return JsonSerializer.Deserialize<Article>(rawBody, FinlightJson.Options)
                ?? throw new FinlightWebhookVerificationException("Invalid JSON payload");
        }
        catch (JsonException)
        {
            throw new FinlightWebhookVerificationException("Invalid JSON payload");
        }
    }

    private static void VerifyTimestamp(string timestamp, TimeProvider timeProvider)
    {
        if (!DateTimeOffset.TryParse(
                timestamp,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
        {
            throw new FinlightWebhookVerificationException("Invalid timestamp format");
        }

        var age = timeProvider.GetUtcNow() - parsed;
        if (age > ReplayTolerance || age < -ReplayTolerance)
        {
            throw new FinlightWebhookVerificationException("Webhook timestamp outside allowed tolerance");
        }
    }
}
