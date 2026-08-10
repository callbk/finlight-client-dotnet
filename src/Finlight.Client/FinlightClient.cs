using Finlight.Http;
using Finlight.WebSockets;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Finlight;

/// <summary>
/// Entry point to the finlight API: REST search via <see cref="Articles"/> and
/// <see cref="Sources"/>, real-time streaming via <see cref="WebSocket"/> and
/// <see cref="RawWebSocket"/>. For streaming with custom options construct
/// <see cref="ArticleWebSocketClient"/> or <see cref="RawArticleWebSocketClient"/>
/// directly.
/// </summary>
public sealed class FinlightClient : IDisposable
{
    private readonly HttpClient? _ownedHttpClient;

    /// <summary>Creates a client with default options.</summary>
    public FinlightClient(string apiKey)
        : this(new FinlightClientOptions { ApiKey = apiKey })
    {
    }

    /// <summary>
    /// Creates a client that owns its own <see cref="HttpClient"/>. Dispose the
    /// client when done.
    /// </summary>
    public FinlightClient(FinlightClientOptions options, ILoggerFactory? loggerFactory = null)
        : this(options, CreateOwnedHttpClient(), loggerFactory, ownsHttpClient: true)
    {
    }

    /// <summary>
    /// Creates a client using a caller-owned <see cref="HttpClient"/> (e.g. from
    /// <c>IHttpClientFactory</c>). The client never disposes it. The per-attempt
    /// timeout is enforced by this library, so prefer an HttpClient whose own
    /// <see cref="HttpClient.Timeout"/> is not shorter than
    /// <see cref="FinlightClientOptions.Timeout"/>.
    /// </summary>
    public FinlightClient(FinlightClientOptions options, HttpClient httpClient, ILoggerFactory? loggerFactory = null)
        : this(options, httpClient, loggerFactory, ownsHttpClient: false)
    {
    }

    private FinlightClient(
        FinlightClientOptions options,
        HttpClient httpClient,
        ILoggerFactory? loggerFactory,
        bool ownsHttpClient)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new ArgumentException("finlight: FinlightClientOptions.ApiKey is required.", nameof(options));
        }

        _ownedHttpClient = ownsHttpClient ? httpClient : null;
        loggerFactory ??= NullLoggerFactory.Instance;

        var api = new ApiClient(options, httpClient, loggerFactory.CreateLogger<FinlightClient>());
        Articles = new ArticleService(api);
        Sources = new SourceService(api);
        WebSocket = new ArticleWebSocketClient(options, wsOptions: null, loggerFactory);
        RawWebSocket = new RawArticleWebSocketClient(options, wsOptions: null, loggerFactory);
    }

    /// <summary>Article search.</summary>
    public ArticleService Articles { get; }

    /// <summary>Source listing.</summary>
    public SourceService Sources { get; }

    /// <summary>Enhanced article stream, default WebSocket options.</summary>
    public ArticleWebSocketClient WebSocket { get; }

    /// <summary>Raw article stream, default WebSocket options.</summary>
    public RawArticleWebSocketClient RawWebSocket { get; }

    /// <summary>Disposes the internally created HttpClient, if any.</summary>
    public void Dispose() => _ownedHttpClient?.Dispose();

    private static HttpClient CreateOwnedHttpClient()
        // The per-attempt timeout is enforced via CancellationToken in ApiClient;
        // HttpClient.Timeout would race it and produce confusing exceptions.
        => new() { Timeout = Timeout.InfiniteTimeSpan };
}
