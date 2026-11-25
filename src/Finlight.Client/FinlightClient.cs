using System.Collections.Concurrent;
using Finlight.Internal;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Finlight;

/// <summary>
/// The client for finlight API v3: REST (<see cref="Articles"/>, <see cref="Companies"/>,
/// <see cref="Sources"/>) and real-time streams (<see cref="StreamAsync"/>, <see cref="RawStreamAsync"/>).
/// Safe for concurrent use; several streams may run at once. Webhooks are verified with the static
/// <see cref="Webhooks"/> class.
/// </summary>
public sealed class FinlightClient : IDisposable
{
    /// <summary>The payload version this SDK speaks on every channel.</summary>
    public const int PayloadVersion = 3;

    private readonly FinlightConfig _config;
    private readonly HttpTransport _http;
    private readonly WsConnector _ws;
    private readonly ILogger _log;
    private readonly ConcurrentDictionary<StreamCore, byte> _streams = new();
    private int _disposed;

    /// <summary>Creates a client with the default settings.</summary>
    /// <param name="apiKey">The API key (required, not empty).</param>
    public FinlightClient(string apiKey)
        : this(new FinlightConfig { ApiKey = apiKey })
    {
    }

    /// <summary>Creates a client.</summary>
    /// <param name="config">The settings; <see cref="FinlightConfig.ApiKey"/> is required.</param>
    public FinlightClient(FinlightConfig config)
    {
        Requests.NotNull(config, "config");
        if (string.IsNullOrWhiteSpace(config.ApiKey))
        {
            throw new FinlightValidationException("FinlightConfig.ApiKey is required and must not be empty", "apiKey");
        }

        Check(Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var baseUri) && baseUri.Scheme is "http" or "https", "baseUrl", "an absolute http(s) URL");
        Check(Uri.TryCreate(config.WssUrl, UriKind.Absolute, out var wssUri) && wssUri.Scheme is "ws" or "wss", "wssUrl", "an absolute ws(s) URL");
        Check(config.MaxPages >= 1, "maxPages", "at least 1");
        Check(config.MaxRetries >= 0, "maxRetries", "0 or more");
        Check(config.MaxMessageBytes >= 16L * 1024 * 1024, "maxMessageBytes", "at least 16 MiB");
        Check(config.MaxResponseBytes > 0, "maxResponseBytes", "positive");
        Check(config.PingInterval > TimeSpan.Zero && config.WatchdogTimeout > TimeSpan.Zero && config.RotationInterval > TimeSpan.Zero, "pingInterval", "positive intervals");
        Check(config.SearchFailedThreshold >= 1, "searchFailedThreshold", "at least 1");
        CheckWait(config.Timeout, "timeout", positive: true, infiniteAllowed: true);
        CheckWait(config.ConnectTimeout, "connectTimeout", positive: true, infiniteAllowed: true);
        if (config.TotalTimeout is { } totalTimeout)
        {
            CheckWait(totalTimeout, "totalTimeout", positive: true, infiniteAllowed: false);
        }

        CheckWait(config.RetryBaseDelay, "retryBaseDelay", positive: false);
        CheckWait(config.RetryMaxDelay, "retryMaxDelay", positive: false);
        CheckWait(config.BurstWait, "burstWait", positive: false);
        CheckWait(config.PingInterval, "pingInterval", positive: true);
        CheckWait(config.WatchdogTimeout, "watchdogTimeout", positive: true);
        CheckWait(config.RotationInterval, "rotationInterval", positive: true);
        CheckWait(config.ReconnectBaseDelay, "reconnectBaseDelay", positive: true);
        CheckWait(config.ReconnectMaxDelay, "reconnectMaxDelay", positive: true);
        CheckWait(config.LongBackoff, "longBackoff", positive: true);
        CheckWait(config.QuotaReconnectInterval, "quotaReconnectInterval", positive: true);

        _config = config;
        _log = (config.LoggerFactory ?? NullLoggerFactory.Instance).CreateLogger("Finlight");
        _http = new HttpTransport(config, _log);
        _ws = new WsConnector(config.HttpClient);
        Articles = new ArticlesService(_http);
        Companies = new CompaniesService(_http);
        Sources = new SourcesService(_http);
    }

    /// <summary>The SDK version (also sent as <c>x-client-version</c>).</summary>
    public static string Version => ClientInfo.Version;

    /// <summary>Article search and lookups.</summary>
    public ArticlesService Articles { get; }

    /// <summary>Company master data (needs <c>entityAccess</c>).</summary>
    public CompaniesService Companies { get; }

    /// <summary>The source catalog.</summary>
    public SourcesService Sources { get; }

    /// <summary>
    /// The enriched stream (<c>wss://wss.finlight.me</c>, <c>payloadVersion</c> 3). The parameters are
    /// checked now; the connection opens when you start iterating. Iterate once with <c>await foreach</c>.
    /// </summary>
    /// <param name="parameters">The subscribe; only keys you set are sent.</param>
    /// <param name="options">Takeover and hooks.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The stream.</returns>
    public FinlightStream StreamAsync(StreamParams parameters, StreamOptions? options = null, CancellationToken cancellationToken = default)
    {
        var subscribe = Requests.StreamSubscribe(parameters);
        return Track(new StreamCore(_config, _log, _ws, raw: false, subscribe, options ?? new StreamOptions(), Untrack), cancellationToken);
    }

    /// <summary>
    /// The raw stream (<c>/raw</c>): articles before enrichment, skeleton only. Raw has no <c>include</c>.
    /// </summary>
    /// <param name="parameters">The subscribe.</param>
    /// <param name="options">Takeover and hooks.</param>
    /// <param name="cancellationToken">Stops the stream.</param>
    /// <returns>The stream.</returns>
    public FinlightStream RawStreamAsync(RawStreamParams parameters, StreamOptions? options = null, CancellationToken cancellationToken = default)
    {
        var subscribe = Requests.RawSubscribe(parameters);
        return Track(new StreamCore(_config, _log, _ws, raw: true, subscribe, options ?? new StreamOptions(), Untrack), cancellationToken);
    }

    /// <summary>
    /// Closes the client: every running stream of this client is closed (sockets with 1000, timers
    /// stopped, its iteration ends) and the HTTP resources the SDK created are released. An injected
    /// <see cref="HttpClient"/> stays open.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var stream in _streams.Keys)
        {
            stream.RequestStop();
        }

        _http.Dispose();
        _ws.Dispose();
    }

    /// <summary>Hides the API key in debug output.</summary>
    /// <returns>A description without secrets.</returns>
    public override string ToString() => $"FinlightClient {{ BaseUrl = {_config.BaseUrl}, Version = {Version} }}";

    private FinlightStream Track(StreamCore core, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        _streams.TryAdd(core, 0);
        if (Volatile.Read(ref _disposed) != 0)
        {
            // Dispose ran in between: it may have missed this stream.
            core.RequestStop();
        }

        return new FinlightStream(core, cancellationToken);
    }

    private void Untrack(StreamCore core) => _streams.TryRemove(core, out _);

    /// <summary>Durations must fit the .NET timers (about 24 days), so a bad value fails here, not in a wait.</summary>
    private static void CheckWait(TimeSpan value, string param, bool positive, bool infiniteAllowed = false)
    {
        if (infiniteAllowed && value == System.Threading.Timeout.InfiniteTimeSpan)
        {
            return;
        }

        var ok = (positive ? value > TimeSpan.Zero : value >= TimeSpan.Zero) && value <= Limits.MaxWait;
        Check(ok, param, (positive ? "positive" : "0 or more") + " and at most " + Limits.MaxWait.TotalDays.ToString("0", System.Globalization.CultureInfo.InvariantCulture) + " days");
    }

    private static void Check(bool ok, string param, string what)
    {
        if (!ok)
        {
            throw new FinlightValidationException($"FinlightConfig.{char.ToUpperInvariant(param[0])}{param[1..]} must be {what}", param);
        }
    }
}
