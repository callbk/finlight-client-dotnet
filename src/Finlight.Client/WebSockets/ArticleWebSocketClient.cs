using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Finlight.WebSockets;

/// <summary>
/// Streams enriched articles in real time. Duplicate articles (same link
/// within the last 10 deliveries) are suppressed.
/// </summary>
public sealed class ArticleWebSocketClient
{
    private readonly WebSocketCore<Article> _core;

    /// <summary>Creates a streaming client. <see cref="FinlightClient"/> exposes one with default options.</summary>
    public ArticleWebSocketClient(
        FinlightClientOptions options,
        FinlightWebSocketOptions? wsOptions = null,
        ILoggerFactory? loggerFactory = null)
    {
        var log = (loggerFactory ?? NullLoggerFactory.Instance).CreateLogger<ArticleWebSocketClient>();
        _core = new WebSocketCore<Article>(
            options,
            wsOptions ?? new FinlightWebSocketOptions(),
            options.WssUrl,
            article => article.Link,
            log);
    }

    /// <summary>
    /// Connects to the finlight WebSocket and yields articles matching
    /// <paramref name="parameters"/>. Reconnects (exponential backoff, proactive
    /// rotation, rate-limit waits) are handled internally. The stream ends
    /// normally when the server preempts this client (another connection took
    /// over the slot); end it yourself by breaking out of the loop or
    /// cancelling <paramref name="cancellationToken"/>.
    /// </summary>
    /// <exception cref="FinlightBlockedException">The server permanently rejected the connection.</exception>
    /// <exception cref="OperationCanceledException"><paramref name="cancellationToken"/> was cancelled.</exception>
    /// <exception cref="InvalidOperationException">A stream is already active on this instance.</exception>
    public IAsyncEnumerable<Article> StreamAsync(
        GetArticlesWebSocketParams parameters,
        CancellationToken cancellationToken = default)
        => _core.StreamAsync(parameters, cancellationToken);
}
