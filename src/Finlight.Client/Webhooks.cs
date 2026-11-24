using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finlight.Internal;
using Microsoft.Extensions.Primitives;

namespace Finlight;

/// <summary>
/// The result of <see cref="Webhooks.ConstructEvent(byte[], IEnumerable{KeyValuePair{string, StringValues}}, string, WebhookVerifyOptions?)"/>:
/// a <see cref="WebhookEvent"/>, or <see cref="WrongPayloadVersion"/> for a body that is not
/// <c>payloadVersion</c> 3. Neither is an error; answer both with 2xx.
/// </summary>
public abstract record WebhookPayload
{
    private protected WebhookPayload()
    {
    }
}

/// <summary>A verified v3 webhook delivery (the envelope). Deduplicate on <see cref="EventId"/>.</summary>
public sealed record WebhookEvent : WebhookPayload
{
    /// <summary><c>eventId</c>: the idempotency key, the same on every retry.</summary>
    [JsonPropertyName("eventId")]
    public required string EventId { get; init; }

    /// <summary><c>event</c>: <c>article.created</c> or <c>article.updated</c> (open).</summary>
    [JsonPropertyName("event")]
    public required WebhookEventName Event { get; init; }

    /// <summary><c>occurredAt</c> as sent; stable across retries.</summary>
    [JsonPropertyName("occurredAt")]
    public string? OccurredAt { get; init; }

    /// <summary><c>payloadVersion</c>: 3.</summary>
    [JsonPropertyName("payloadVersion")]
    public int PayloadVersion { get; init; }

    /// <summary><c>subscriptionId</c>: the webhook id.</summary>
    [JsonPropertyName("subscriptionId")]
    public string? SubscriptionId { get; init; }

    /// <summary><c>changedFields</c>: only on some <c>article.updated</c>; null when absent.</summary>
    [JsonPropertyName("changedFields")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyList<string>? ChangedFields { get; init; }

    /// <summary><c>data</c>: the article.</summary>
    [JsonPropertyName("data")]
    public required Article Data { get; init; }

    /// <summary>True for a dashboard test delivery (<c>eventId</c> starts with <c>evt_test</c>); it ignores entitlements.</summary>
    [JsonIgnore]
    public bool IsTest => EventId.StartsWith("evt_test", StringComparison.Ordinal);

    /// <summary><see cref="OccurredAt"/> parsed, or null.</summary>
    [JsonIgnore]
    public DateTimeOffset? OccurredAtTime => Rfc3339.Parse(OccurredAt);

    internal string? RawJson { get; set; }

    /// <summary>The envelope exactly as received.</summary>
    /// <returns>The JSON text, or null for an event built in code.</returns>
    public string? GetRawJson() => RawJson;
}

/// <summary>
/// A correctly signed body that is not <c>payloadVersion</c> 3, e.g. a v2 delivery still queued after
/// you switched the webhook. Not an error: answer it with 2xx and keep <see cref="Raw"/>.
/// </summary>
public sealed record WrongPayloadVersion : WebhookPayload
{
    internal WrongPayloadVersion(JsonElement? payloadVersion, string raw, string? link)
    {
        PayloadVersion = payloadVersion;
        Raw = raw;
        Link = link;
    }

    /// <summary>The version by the server's rule: always 2 here.</summary>
    public int DetectedVersion => 2;

    /// <summary>The raw <c>payloadVersion</c> value as received; null when the key was absent.</summary>
    public JsonElement? PayloadVersion { get; }

    /// <summary>True when the key <c>payloadVersion</c> was in the body.</summary>
    public bool PayloadVersionPresent => PayloadVersion is not null;

    /// <summary>The whole body as JSON text.</summary>
    public string Raw { get; }

    /// <summary>The article link, when it can be read (v2 <c>link</c> or <c>data.link</c>).</summary>
    public string? Link { get; }

    /// <summary>The body as received.</summary>
    /// <returns>The JSON text.</returns>
    public string GetRawJson() => Raw;
}

/// <summary>Options of the webhook checks.</summary>
public sealed record WebhookVerifyOptions
{
    /// <summary>Largest allowed distance between <c>X-Webhook-Timestamp</c> and now, both directions. Default 300 s.</summary>
    public TimeSpan Tolerance { get; init; } = Webhooks.DefaultTolerance;

    /// <summary>The clock. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider Now { get; init; } = TimeProvider.System;
}

/// <summary>
/// Verifies and reads incoming webhooks (R-HOOK-1 to R-HOOK-3). Pass the raw body bytes exactly as
/// received, never re-serialized JSON. Order: signature, time, parse, payload version, decode.
/// </summary>
public static class Webhooks
{
    /// <summary>The default tolerance: 300 s.</summary>
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromSeconds(300);

    private const string SignatureHeader = "X-Webhook-Signature";
    private const string TimestampHeader = "X-Webhook-Timestamp";

    /// <summary>Verifies and reads a webhook; headers as name/values pairs (e.g. ASP.NET Core <c>Request.Headers</c>).</summary>
    /// <param name="rawBody">The body bytes exactly as received.</param>
    /// <param name="headers">The request headers; names are matched case-insensitively.</param>
    /// <param name="secret">The webhook secret (64 hex characters, used as UTF-8 text).</param>
    /// <param name="options">Tolerance and clock.</param>
    /// <returns>The event, or <see cref="WrongPayloadVersion"/>.</returns>
    /// <exception cref="WebhookVerificationException">The signature, timestamp or headers are not valid.</exception>
    /// <exception cref="FinlightDecodeException">A validly signed body is not a v3 envelope.</exception>
    public static WebhookPayload ConstructEvent(
        byte[] rawBody,
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        string secret,
        WebhookVerifyOptions? options = null)
    {
        if (headers is null)
        {
            throw Fail(WebhookVerificationReason.MissingSignature, "no headers were given");
        }

        var signature = Header(headers.Select(h => new KeyValuePair<string, IEnumerable<string?>>(h.Key, h.Value)), SignatureHeader);
        var timestamp = Header(headers.Select(h => new KeyValuePair<string, IEnumerable<string?>>(h.Key, h.Value)), TimestampHeader);
        return ConstructEvent(rawBody, signature, timestamp, secret, options);
    }

    /// <summary>Verifies and reads a webhook; headers from an <see cref="HttpRequestMessage"/> or similar.</summary>
    /// <param name="rawBody">The body bytes exactly as received.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="options">Tolerance and clock.</param>
    /// <returns>The event, or <see cref="WrongPayloadVersion"/>.</returns>
    public static WebhookPayload ConstructEvent(byte[] rawBody, HttpHeaders headers, string secret, WebhookVerifyOptions? options = null)
    {
        if (headers is null)
        {
            throw Fail(WebhookVerificationReason.MissingSignature, "no headers were given");
        }

        var pairs = headers.NonValidated.Select(h => new KeyValuePair<string, IEnumerable<string?>>(h.Key, h.Value)).ToList();
        return ConstructEvent(rawBody, Header(pairs, SignatureHeader), Header(pairs, TimestampHeader), secret, options);
    }

    /// <summary>Verifies and reads a webhook given as a UTF-8 string (the raw text, not re-serialized).</summary>
    /// <param name="rawBody">The body text exactly as received.</param>
    /// <param name="headers">The request headers.</param>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="options">Tolerance and clock.</param>
    /// <returns>The event, or <see cref="WrongPayloadVersion"/>.</returns>
    public static WebhookPayload ConstructEvent(
        string rawBody,
        IEnumerable<KeyValuePair<string, StringValues>> headers,
        string secret,
        WebhookVerifyOptions? options = null)
        => ConstructEvent(BodyBytes(rawBody), headers, secret, options);

    /// <summary>Verifies and reads a webhook from the two header values.</summary>
    /// <param name="rawBody">The body bytes exactly as received.</param>
    /// <param name="signature">The <c>X-Webhook-Signature</c> value (<c>sha256=&lt;hex&gt;</c>).</param>
    /// <param name="timestamp">The <c>X-Webhook-Timestamp</c> value (ISO 8601).</param>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="options">Tolerance and clock.</param>
    /// <returns>The event, or <see cref="WrongPayloadVersion"/>.</returns>
    public static WebhookPayload ConstructEvent(
        byte[] rawBody,
        string? signature,
        string? timestamp,
        string secret,
        WebhookVerifyOptions? options = null)
    {
        VerifySignature(rawBody, signature, timestamp, secret, options);
        return Read(rawBody);
    }

    /// <summary>Verifies and reads a webhook from the two header values, body as text.</summary>
    /// <param name="rawBody">The body text exactly as received.</param>
    /// <param name="signature">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="timestamp">The <c>X-Webhook-Timestamp</c> value.</param>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="options">Tolerance and clock.</param>
    /// <returns>The event, or <see cref="WrongPayloadVersion"/>.</returns>
    public static WebhookPayload ConstructEvent(
        string rawBody,
        string? signature,
        string? timestamp,
        string secret,
        WebhookVerifyOptions? options = null)
        => ConstructEvent(BodyBytes(rawBody), signature, timestamp, secret, options);

    /// <summary>
    /// Checks the signature and the timestamp only: HMAC-SHA256 over <c>timestamp + "." + body</c>, keyed
    /// with the UTF-8 bytes of the secret, compared in constant time.
    /// </summary>
    /// <param name="rawBody">The body bytes exactly as received.</param>
    /// <param name="signature">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="timestamp">The <c>X-Webhook-Timestamp</c> value.</param>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="options">Tolerance and clock.</param>
    /// <exception cref="WebhookVerificationException">The check failed.</exception>
    public static void VerifySignature(
        byte[] rawBody,
        string? signature,
        string? timestamp,
        string secret,
        WebhookVerifyOptions? options = null)
    {
        if (rawBody is null)
        {
            throw Fail(WebhookVerificationReason.InvalidRawBody, "the raw body is missing; pass the bytes exactly as received");
        }

        if (string.IsNullOrEmpty(secret))
        {
            throw Fail(WebhookVerificationReason.InvalidSecret, "the webhook secret is empty");
        }

        if (string.IsNullOrEmpty(signature))
        {
            throw Fail(WebhookVerificationReason.MissingSignature, "the X-Webhook-Signature header is missing");
        }

        if (string.IsNullOrEmpty(timestamp))
        {
            throw Fail(WebhookVerificationReason.MissingTimestamp, "the X-Webhook-Timestamp header is missing");
        }

        if (!IsAscii(signature) || !IsAscii(timestamp))
        {
            throw Fail(WebhookVerificationReason.NonAsciiHeader, "a webhook header holds characters outside ASCII");
        }

        if (!signature.StartsWith("sha256=", StringComparison.Ordinal))
        {
            throw Fail(WebhookVerificationReason.UnsupportedSignatureScheme, "the signature is not of the form sha256=<hex>");
        }

        // Fail closed on a timestamp that cannot be read, before any other check passes.
        var sentAt = Rfc3339.Parse(timestamp)
            ?? throw Fail(WebhookVerificationReason.UnparseableTimestamp, "X-Webhook-Timestamp is not an ISO 8601 time with a zone");
        var expected = new byte[64];
        var secretBytes = Encoding.UTF8.GetBytes(secret);
        var message = new byte[Encoding.ASCII.GetByteCount(timestamp) + 1 + rawBody.Length];
        var offset = Encoding.ASCII.GetBytes(timestamp, message);
        message[offset] = (byte)'.';
        rawBody.CopyTo(message, offset + 1);
        var mac = HMACSHA256.HashData(secretBytes, message);
        Encoding.ASCII.GetBytes(Convert.ToHexString(mac).ToLowerInvariant(), expected);
        var given = Encoding.ASCII.GetBytes(signature[7..]);
        if (given.Length != expected.Length || !CryptographicOperations.FixedTimeEquals(given, expected))
        {
            throw Fail(WebhookVerificationReason.SignatureMismatch, "the signature does not match the body");
        }

        var tolerance = options?.Tolerance ?? DefaultTolerance;
        var now = (options?.Now ?? TimeProvider.System).GetUtcNow();
        if ((now - sentAt).Duration() > tolerance)
        {
            throw Fail(WebhookVerificationReason.TimestampOutsideTolerance, $"X-Webhook-Timestamp is more than {tolerance} away from now");
        }
    }

    /// <summary>Checks the signature and the timestamp of a body given as text.</summary>
    /// <param name="rawBody">The body text exactly as received.</param>
    /// <param name="signature">The <c>X-Webhook-Signature</c> value.</param>
    /// <param name="timestamp">The <c>X-Webhook-Timestamp</c> value.</param>
    /// <param name="secret">The webhook secret.</param>
    /// <param name="options">Tolerance and clock.</param>
    public static void VerifySignature(
        string rawBody,
        string? signature,
        string? timestamp,
        string secret,
        WebhookVerifyOptions? options = null)
        => VerifySignature(BodyBytes(rawBody), signature, timestamp, secret, options);

    private static WebhookPayload Read(byte[] rawBody)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(rawBody);
        }
        catch (JsonException exception)
        {
            throw new FinlightDecodeException("the webhook body is not JSON", "$", null, exception);
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new FinlightDecodeException("the webhook body is not a JSON object", "$");
            }

            var raw = Encoding.UTF8.GetString(rawBody);
            JsonElement? version = root.TryGetProperty("payloadVersion", out var v) ? v.Clone() : null;
            // The server's rule: only the JSON number 3 (also 3.0) is v3; no bool, no string.
            if (version is not { ValueKind: JsonValueKind.Number } number || !number.TryGetDouble(out var value) || value != 3)
            {
                return new WrongPayloadVersion(version, raw, LegacyLink(root));
            }

            try
            {
                var envelope = root.Deserialize<WebhookEnvelope>(FinlightJson.Options)
                    ?? throw new FinlightDecodeException("the webhook body is not an object", "$");
                Require.NotNull(envelope.EventId, "eventId");
                Require.NotNull(envelope.Data, "data");
                var data = Decode.Article(root.GetProperty("data"), "$.data");
                return new WebhookEvent
                {
                    EventId = envelope.EventId!,
                    Event = envelope.Event,
                    OccurredAt = envelope.OccurredAt,
                    PayloadVersion = 3,
                    SubscriptionId = envelope.SubscriptionId,
                    ChangedFields = envelope.ChangedFields,
                    Data = data,
                    RawJson = raw,
                };
            }
            catch (JsonException exception)
            {
                throw new FinlightDecodeException($"the webhook envelope cannot be read: {exception.Message}", exception.Path, null, exception);
            }
        }
    }

    /// <summary>The article link of a body that is not v3 (v2 bodies carry <c>link</c> at the top or in <c>data</c>).</summary>
    private static string? LegacyLink(JsonElement root)
        => ErrorParser.Str(root, "link")
            ?? (root.TryGetProperty("data", out var data) ? ErrorParser.Str(data, "link") : null)
            ?? (root.TryGetProperty("article", out var article) ? ErrorParser.Str(article, "link") : null);

    private static string? Header(IEnumerable<KeyValuePair<string, IEnumerable<string?>>> headers, string name)
    {
        string? found = null;
        var count = 0;
        foreach (var (key, values) in headers)
        {
            var candidate = key.Replace('-', '_');
            if (!string.Equals(key, name, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(candidate, "HTTP_" + name.Replace('-', '_'), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            foreach (var value in values ?? [])
            {
                if (value is null)
                {
                    continue;
                }

                count++;
                found = value;
            }
        }

        if (count > 1)
        {
            throw Fail(WebhookVerificationReason.DuplicateHeader, $"the {name} header appears more than once");
        }

        return found;
    }

    private static byte[] BodyBytes(string rawBody)
        => rawBody is null
            ? throw Fail(WebhookVerificationReason.InvalidRawBody, "the raw body is missing")
            : Encoding.UTF8.GetBytes(rawBody);

    private static bool IsAscii(string value)
    {
        foreach (var c in value)
        {
            if (c > 0x7E || c < 0x20)
            {
                return false;
            }
        }

        return true;
    }

    private static WebhookVerificationException Fail(WebhookVerificationReason reason, string message)
        => new(reason, "webhook verification failed: " + message);

    private sealed record WebhookEnvelope
    {
        [JsonPropertyName("eventId")]
        public string? EventId { get; init; }

        [JsonPropertyName("event")]
        public WebhookEventName Event { get; init; }

        [JsonPropertyName("occurredAt")]
        public string? OccurredAt { get; init; }

        [JsonPropertyName("subscriptionId")]
        public string? SubscriptionId { get; init; }

        [JsonPropertyName("changedFields")]
        public IReadOnlyList<string>? ChangedFields { get; init; }

        [JsonPropertyName("data")]
        public JsonElement? Data { get; init; }
    }
}
