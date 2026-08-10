using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Finlight.WebSockets;

/// <summary>
/// Streams unenriched articles in real time (no sentiment, entities, or
/// content — lower latency). No duplicate suppression.
/// </summary>
public sealed class RawArticleWebSocketClient
{
    private readonly WebSocketCore<RawArticle> _core;

    /// <summary>Creates a raw streaming client. <see cref="FinlightClient"/> exposes one with default options.</summary>
    public RawArticleWebSocketClient(
        FinlightClientOptions options,
        FinlightWebSocketOptions? wsOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        var log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<RawArticleWebSocketClient>();
        _core = new WebSocketCore<RawArticle>(
            options,
            wsOptions ?? new FinlightWebSocketOptions(),
            options.WssUrl + "/raw",
            identify: null,
            log);
    }

    /// <summary>
    /// Connects to the raw finlight WebSocket and yields articles matching
    /// <paramref name="parameters"/>. See <see cref="ArticleWebSocketClient.StreamAsync"/>
    /// for the streaming semantics.
    /// </summary>
    /// <exception cref="FinlightBlockedException">The server permanently rejected the connection.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A stream is already active on this instance.</exception>
    public IAsyncEnumerable<RawArticle> StreamAsync(
        GetRawArticlesWebSocketParams parameters,
        CancellationToken cancellationToken = default)
        => _core.StreamAsync(parameters, cancellationToken);
}
