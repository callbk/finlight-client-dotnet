using Microsoft.Extensions.Logging;

namespace Finlight;

/// <summary>What a REST call does on a burst 429.</summary>
public enum RetryOnBurst
{
    /// <summary>Wait <c>Retry-After</c> (or <see cref="FinlightConfig.BurstWait"/>) and retry once. The default.</summary>
    Once = 0,

    /// <summary>Throw the <see cref="RateLimitedException"/> at once.</summary>
    Off = 1,
}

/// <summary>
/// Settings of a <see cref="FinlightClient"/>. Only <see cref="ApiKey"/> is required; every other
/// value has the default shown. Times are <see cref="TimeSpan"/>.
/// </summary>
public sealed record FinlightConfig
{
    /// <summary>The API key (required, not empty). Sent as <c>X-API-KEY</c> (REST) and <c>x-api-key</c> (WebSocket).</summary>
    public required string ApiKey { get; init; }

    /// <summary>REST base URL. Default <c>https://api.finlight.me</c>; a trailing slash is fine.</summary>
    public string BaseUrl { get; init; } = "https://api.finlight.me";

    /// <summary>WebSocket URL. Default <c>wss://wss.finlight.me</c>; the raw stream uses <c>/raw</c> below it.</summary>
    public string WssUrl { get; init; } = "wss://wss.finlight.me";

    /// <summary>Timeout of one REST attempt. Default 30 s. Not applied to an injected <see cref="HttpClient"/>.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Optional limit for a whole REST call, retries and burst wait included. Default none.</summary>
    public TimeSpan? TotalTimeout { get; init; }

    /// <summary>Retries after 5xx, network errors and attempt timeouts. Default 3.</summary>
    public int MaxRetries { get; init; } = 3;

    /// <summary>First retry delay; doubles each retry, with jitter. Default 0.5 s.</summary>
    public TimeSpan RetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>Largest retry delay. Default 8 s.</summary>
    public TimeSpan RetryMaxDelay { get; init; } = TimeSpan.FromSeconds(8);

    /// <summary>Burst 429: wait and retry once (default) or throw at once. Not counted in <see cref="MaxRetries"/>.</summary>
    public RetryOnBurst RetryOnBurst { get; init; } = RetryOnBurst.Once;

    /// <summary>Burst wait when the 429 has no <c>Retry-After</c> header. Default 10 s.</summary>
    public TimeSpan BurstWait { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>Default page limit of <c>SearchAllAsync</c>. Default 10.</summary>
    public int MaxPages { get; init; } = 10;

    /// <summary>Largest REST response body. Default 32 MiB.</summary>
    public long MaxResponseBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Records kept for ETag revalidation in <c>companies.get</c>. Default 256; 0 turns the cache off.</summary>
    public int CompanyCacheSize { get; init; } = 256;

    /// <summary>Interval of the stream's application ping. Default 25 s.</summary>
    public TimeSpan PingInterval { get; init; } = TimeSpan.FromSeconds(25);

    /// <summary>A stream connection without any pong for this long is replaced. Default 60 s.</summary>
    public TimeSpan WatchdogTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A stream connection is renewed after this long (gateway limit 2 h). Default 115 min.</summary>
    public TimeSpan RotationInterval { get; init; } = TimeSpan.FromMinutes(115);

    /// <summary>First reconnect delay of a stream; doubles, with jitter. Default 1 s.</summary>
    public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Largest reconnect delay of a stream. Default 30 s.</summary>
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Wait after a connect refused for the connection limit or a 429. Default 60 s.</summary>
    public TimeSpan LongBackoff { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Exhausted WebSocket quota: keep retrying every <see cref="QuotaReconnectInterval"/> (default on)
    /// instead of ending the stream. Each wait goes to <see cref="StreamOptions.OnQuotaWait"/>.</summary>
    public bool QuotaReconnect { get; init; } = true;

    /// <summary>Retry interval while the WebSocket quota is exhausted. Default 1 h.</summary>
    public TimeSpan QuotaReconnectInterval { get; init; } = TimeSpan.FromHours(1);

    /// <summary><c>SEARCH_FAILED</c> in a row before one goes to <see cref="StreamOptions.OnError"/>. Default 5.</summary>
    public int SearchFailedThreshold { get; init; } = 5;

    /// <summary>Stream items remembered to drop duplicates. Default 1000.</summary>
    public int DedupeCacheSize { get; init; } = 1000;

    /// <summary>Largest incoming stream message. Default 32 MiB (at least 16 MiB).</summary>
    public long MaxMessageBytes { get; init; } = 32L * 1024 * 1024;

    /// <summary>Timeout of the WebSocket handshake. Default 30 s.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Logger factory; the SDK logs under the category <c>Finlight</c>. Default: silent.</summary>
    public ILoggerFactory? LoggerFactory { get; init; }

    /// <summary>Clock and timers (retry and burst waits, stream timers). Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    /// <summary>
    /// Your own HTTP client for REST and the WebSocket handshake. The SDK never disposes it, and its
    /// timeouts apply. Do not add resilience handlers (Polly): the SDK already retries.
    /// </summary>
    public HttpClient? HttpClient { get; init; }

    internal Func<double> Random { get; init; } = System.Random.Shared.NextDouble;

    internal Func<string>? NonceFactory { get; init; }

    /// <summary>Hides the API key in debug output.</summary>
    /// <returns>A description without secrets.</returns>
    public override string ToString() => $"FinlightConfig {{ BaseUrl = {BaseUrl}, WssUrl = {WssUrl}, ApiKey = *** }}";
}
