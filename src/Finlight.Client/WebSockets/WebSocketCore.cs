using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Finlight.Json;
using Microsoft.Extensions.Logging;

namespace Finlight.WebSockets;

/// <summary>How one receive step of a connection ended.</summary>
internal enum StreamOutcome
{
    /// <summary>An article was received; keep streaming.</summary>
    Item,

    /// <summary>The connection is gone; reconnect with backoff.</summary>
    Reconnect,

    /// <summary>The server preempted this client; end the stream normally.</summary>
    EndOfStream,

    /// <summary>The server permanently rejected the connection (close 1008).</summary>
    Blocked,
}

/// <summary>
/// The shared streaming protocol for both article types: reconnect loop with
/// exponential backoff, application-level ping/pong with watchdog, proactive
/// connection rotation, and optional duplicate suppression.
/// </summary>
internal sealed class WebSocketCore<T>
{
    private const int RecentArticleCacheSize = 10;
    private const int MaxArticleMessageSize = 16 * 1024 * 1024;
    private static readonly TimeSpan WatchdogInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan DialRateLimitBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorRateLimitBackoff = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ErrorBlockedBackoff = TimeSpan.FromHours(1);
    private static readonly TimeSpan DefaultAdminKickRetry = TimeSpan.FromMinutes(15);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan CloseGracePeriod = TimeSpan.FromSeconds(10);

    private readonly FinlightClientOptions _options;
    private readonly FinlightWebSocketOptions _wsOptions;
    private readonly Uri _uri;
    private readonly Func<T, string>? _identify;
    private readonly ILogger _log;
    private readonly TimeProvider _time;

    private readonly Queue<string> _recent = new();
    private readonly HashSet<string> _recentSet = new();
    private DateTimeOffset? _reconnectAt;
    private int _active;

    /// <summary>
    /// A non-null identify function enables suppression of duplicate articles
    /// by the returned key, over the last <see cref="RecentArticleCacheSize"/> deliveries.
    /// </summary>
    public WebSocketCore(
        FinlightClientOptions options,
        FinlightWebSocketOptions wsOptions,
        string url,
        Func<T, string>? identify,
        ILogger log)
    {
        if (string.IsNullOrEmpty(options.ApiKey))
        {
            throw new ArgumentException("finlight: FinlightClientOptions.ApiKey is required.", nameof(options));
        }

        _options = options;
        _wsOptions = wsOptions;
        _uri = new Uri(url);
        _identify = identify;
        _log = log;
        _time = options.TimeProvider;
    }

    public async IAsyncEnumerable<T> StreamAsync(
        object payload,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (Interlocked.CompareExchange(ref _active, 1, 0) != 0)
        {
            throw new InvalidOperationException("finlight: this client supports one active stream at a time.");
        }

        try
        {
            var delay = _wsOptions.BaseReconnectDelay;
            _reconnectAt = null;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                _log.LogInformation("finlight ws: connecting to {Url}", _uri);

                var (connection, connected) = await ConnectAsync(payload, cancellationToken).ConfigureAwait(false);
                if (connection is not null)
                {
                    _reconnectAt = null;
                    var outcome = StreamOutcome.Reconnect;
                    try
                    {
                        while (true)
                        {
                            T? item;
                            (outcome, item) = await connection.ReceiveNextAsync(cancellationToken).ConfigureAwait(false);
                            if (outcome != StreamOutcome.Item)
                            {
                                break;
                            }

                            yield return item!;
                        }
                    }
                    finally
                    {
                        await connection.DisposeAsync().ConfigureAwait(false);
                    }

                    if (outcome == StreamOutcome.EndOfStream)
                    {
                        yield break;
                    }

                    if (outcome == StreamOutcome.Blocked)
                    {
                        throw new FinlightBlockedException();
                    }
                }

                cancellationToken.ThrowIfCancellationRequested();
                if (connected)
                {
                    delay = _wsOptions.BaseReconnectDelay;
                }

                var now = _time.GetUtcNow();
                TimeSpan wait;
                if (_reconnectAt is { } reconnectAt && reconnectAt > now)
                {
                    wait = reconnectAt - now;
                    _log.LogInformation("finlight ws: waiting until reconnectAt ({Wait})", wait);
                }
                else
                {
                    wait = delay;
                    _log.LogInformation("finlight ws: reconnecting in {Delay}", wait);
                    delay = delay * 2 <= _wsOptions.MaxReconnectDelay ? delay * 2 : _wsOptions.MaxReconnectDelay;
                }

                await Task.Delay(wait, _time, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _active, 0);
        }
    }

    /// <summary>
    /// Establishes one connection and sends the handshake. Returns a null
    /// connection on failure; Connected reports whether the socket was opened
    /// (which resets the reconnect backoff, like the sibling clients).
    /// </summary>
    private async Task<(Connection? Connection, bool Connected)> ConnectAsync(
        object payload,
        CancellationToken cancellationToken)
    {
        var socket = new ClientWebSocket();
        // The server answers application-level JSON pings, not protocol ping
        // frames, so the built-in keep-alive must stay off.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.CollectHttpResponseDetails = true;
        // The server reads these headers case-sensitively in exact lowercase;
        // SocketsHttpHandler writes custom header names verbatim on the
        // HTTP/1.1 upgrade, so the casing survives on the wire.
        socket.Options.SetRequestHeader("x-api-key", _options.ApiKey);
        socket.Options.SetRequestHeader("x-client-version", ClientVersion.Value);
        if (_wsOptions.Takeover)
        {
            socket.Options.SetRequestHeader("x-takeover", "true");
        }

        try
        {
            using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            dialCts.CancelAfter(_options.Timeout);
            await socket.ConnectAsync(_uri, dialCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            socket.Dispose();
            throw;
        }
        catch (Exception exception)
        {
            if (socket.HttpStatusCode == HttpStatusCode.TooManyRequests)
            {
                _reconnectAt = _time.GetUtcNow() + DialRateLimitBackoff;
                _log.LogWarning(
                    "finlight ws: server rejected connection (429), backing off {Backoff}", DialRateLimitBackoff);
            }
            else
            {
                _log.LogError(exception, "finlight ws: connection failed");
            }

            socket.Dispose();
            return (null, false);
        }

        _log.LogInformation("finlight ws: connected");
        var connection = new Connection(this, socket);
        try
        {
            await connection.SendHandshakeAsync(payload, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "finlight ws: handshake write failed");
            await connection.DisposeAsync().ConfigureAwait(false);
            return (null, true);
        }

        connection.StartKeepalive();
        return (connection, true);
    }

    private bool IsDuplicate(string id) => _recentSet.Contains(id);

    private void Track(string id)
    {
        _recent.Enqueue(id);
        _recentSet.Add(id);
        if (_recent.Count > RecentArticleCacheSize)
        {
            _recentSet.Remove(_recent.Dequeue());
        }
    }

    /// <summary>The state of one established connection.</summary>
    private sealed class Connection : IAsyncDisposable
    {
        private readonly WebSocketCore<T> _core;
        private readonly ClientWebSocket _socket;
        private readonly CancellationTokenSource _lifetimeCts = new();
        private readonly SemaphoreSlim _sendLock = new(1, 1);
        private readonly string _nonce = Guid.NewGuid().ToString();
        private Task? _keepalive;
        private long _lastPongMs;
        private bool _closeNotified;

        public Connection(WebSocketCore<T> core, ClientWebSocket socket)
        {
            _core = core;
            _socket = socket;
            _lastPongMs = core._time.GetUtcNow().ToUnixTimeMilliseconds();
        }

        public async Task SendHandshakeAsync(object payload, CancellationToken cancellationToken)
        {
            var node = JsonSerializer.SerializeToNode(payload, payload.GetType(), FinlightJson.Options) as JsonObject
                ?? [];
            node["clientNonce"] = _nonce;
            await SendJsonAsync(node, cancellationToken).ConfigureAwait(false);
        }

        public void StartKeepalive() => _keepalive = KeepaliveAsync(_lifetimeCts.Token);

        /// <summary>
        /// Receives and dispatches server messages until an article arrives or
        /// the connection ends. Throws only on caller cancellation.
        /// </summary>
        public async Task<(StreamOutcome Outcome, T? Item)> ReceiveNextAsync(CancellationToken cancellationToken)
        {
            while (true)
            {
                byte[]? data;
                try
                {
                    data = await ReceiveMessageAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _core._log.LogInformation("finlight ws: connection closed ({Error})", exception.Message);
                    NotifyClose((int)(_socket.CloseStatus ?? 0), _socket.CloseStatusDescription ?? "");
                    return (StreamOutcome.Reconnect, default);
                }

                if (data is null)
                {
                    var code = (int)(_socket.CloseStatus ?? 0);
                    var reason = _socket.CloseStatusDescription ?? "";
                    _core._log.LogInformation(
                        "finlight ws: connection closed (code {Code}, reason '{Reason}')", code, reason);
                    NotifyClose(code, reason);
                    if (code == (int)WebSocketCloseStatus.PolicyViolation)
                    {
                        _core._log.LogWarning("finlight ws: connection rejected by server (blocked)");
                        return (StreamOutcome.Blocked, default);
                    }

                    return (StreamOutcome.Reconnect, default);
                }

                var (outcome, item) = await HandleMessageAsync(data, cancellationToken).ConfigureAwait(false);
                if (outcome is { } ended)
                {
                    return (ended, item);
                }
            }
        }

        /// <summary>Reads one complete message; null means a close frame arrived.</summary>
        private async Task<byte[]?> ReceiveMessageAsync(CancellationToken cancellationToken)
        {
            var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
            try
            {
                using var message = new MemoryStream();
                while (true)
                {
                    var result = await _socket.ReceiveAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        return null;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (message.Length > MaxArticleMessageSize)
                    {
                        throw new InvalidOperationException("finlight ws: message exceeds the 16MB read limit.");
                    }

                    if (result.EndOfMessage)
                    {
                        return message.ToArray();
                    }
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        /// <summary>
        /// Dispatches one server message. A null outcome means: keep receiving.
        /// </summary>
        private async Task<(StreamOutcome? Outcome, T? Item)> HandleMessageAsync(
            byte[] data,
            CancellationToken cancellationToken)
        {
            var log = _core._log;
            ServerMessage? message;
            try
            {
                message = JsonSerializer.Deserialize<ServerMessage>(data, FinlightJson.Options);
            }
            catch (JsonException exception)
            {
                log.LogError(exception, "finlight ws: cannot parse message");
                return (null, default);
            }

            switch (message?.Action)
            {
                case "pong":
                    var nowMs = _core._time.GetUtcNow().ToUnixTimeMilliseconds();
                    if (message.T > 0)
                    {
                        log.LogDebug("finlight ws: pong received (rtt {RttMs}ms)", nowMs - message.T);
                    }
                    else
                    {
                        log.LogDebug("finlight ws: pong received");
                    }

                    Volatile.Write(ref _lastPongMs, nowMs);
                    break;

                case "admit":
                    log.LogInformation("finlight ws: admitted (leaseId {LeaseId})", message.LeaseId);
                    if (!string.IsNullOrEmpty(message.ClientNonce) && message.ClientNonce != _nonce)
                    {
                        log.LogWarning(
                            "finlight ws: nonce mismatch (expected {Expected}, got {Got})",
                            _nonce, message.ClientNonce);
                    }

                    break;

                case "preempted":
                    log.LogWarning(
                        "finlight ws: connection preempted (reason '{Reason}', newLeaseId {NewLeaseId})",
                        message.Reason, message.NewLeaseId);
                    await TryCloseOutputAsync(
                        WebSocketCloseStatus.NormalClosure, "Preempted by server", cancellationToken)
                        .ConfigureAwait(false);
                    NotifyClose((int)WebSocketCloseStatus.NormalClosure, "client stopped");
                    return (StreamOutcome.EndOfStream, default);

                case "sendArticle":
                    T? article;
                    try
                    {
                        article = message.Data is { } element
                            ? element.Deserialize<T>(FinlightJson.Options)
                            : default;
                    }
                    catch (JsonException exception)
                    {
                        log.LogError(exception, "finlight ws: cannot parse article");
                        break;
                    }

                    if (article is null)
                    {
                        log.LogError("finlight ws: cannot parse article (empty payload)");
                        break;
                    }

                    if (_core._identify is { } identify)
                    {
                        var id = identify(article);
                        if (_core.IsDuplicate(id))
                        {
                            log.LogDebug("finlight ws: skipping duplicate article ({Id})", id);
                            break;
                        }

                        _core.Track(id);
                    }

                    return (StreamOutcome.Item, article);

                case "admin_kick":
                    var retryAfter = message.RetryAfter > 0
                        ? TimeSpan.FromMilliseconds(message.RetryAfter)
                        : DefaultAdminKickRetry;
                    _core._reconnectAt = _core._time.GetUtcNow() + retryAfter;
                    log.LogWarning("finlight ws: admin kick (retryAfter {RetryAfter})", retryAfter);
                    await TryCloseOutputAsync((WebSocketCloseStatus)4003, "Admin kick", cancellationToken)
                        .ConfigureAwait(false);
                    break;

                case "error":
                    var errorText = RawToString(message.Data);
                    if (errorText.Length == 0)
                    {
                        errorText = RawToString(message.Error);
                    }

                    log.LogError("finlight ws: server error: {Error}", errorText);
                    var lowered = errorText.ToLowerInvariant();
                    if (lowered.Contains("limit"))
                    {
                        _core._reconnectAt = _core._time.GetUtcNow() + ErrorRateLimitBackoff;
                        await TryCloseOutputAsync((WebSocketCloseStatus)4001, "Rate limited", cancellationToken)
                            .ConfigureAwait(false);
                    }
                    else if (lowered.Contains("blocked"))
                    {
                        _core._reconnectAt = _core._time.GetUtcNow() + ErrorBlockedBackoff;
                        await TryCloseOutputAsync((WebSocketCloseStatus)4002, "User blocked", cancellationToken)
                            .ConfigureAwait(false);
                    }

                    break;

                default:
                    log.LogWarning("finlight ws: unknown message action '{Action}'", message?.Action);
                    break;
            }

            return (null, default);
        }

        /// <summary>
        /// Sends application-level pings, watches for missing pongs, and rotates
        /// the connection before the server-side lifetime cap.
        /// </summary>
        private async Task KeepaliveAsync(CancellationToken token)
        {
            try
            {
                await Task.WhenAll(PingLoopAsync(token), WatchdogLoopAsync(token), RotationAsync(token))
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task PingLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(_core._wsOptions.PingInterval, _core._time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                try
                {
                    var ping = new Dictionary<string, object>
                    {
                        ["action"] = "ping",
                        ["t"] = _core._time.GetUtcNow().ToUnixTimeMilliseconds(),
                    };
                    await SendJsonAsync(ping, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _core._log.LogDebug("finlight ws: ping failed ({Error})", exception.Message);
                }
            }
        }

        private async Task WatchdogLoopAsync(CancellationToken token)
        {
            using var timer = new PeriodicTimer(WatchdogInterval, _core._time);
            while (await timer.WaitForNextTickAsync(token).ConfigureAwait(false))
            {
                var sincePong = _core._time.GetUtcNow().ToUnixTimeMilliseconds() - Volatile.Read(ref _lastPongMs);
                if (sincePong > _core._wsOptions.PongTimeout.TotalMilliseconds)
                {
                    _core._log.LogWarning("finlight ws: no pong received in time, forcing reconnect");
                    _socket.Abort();
                    return;
                }
            }
        }

        private async Task RotationAsync(CancellationToken token)
        {
            await Task.Delay(_core._wsOptions.ConnectionLifetime, _core._time, token).ConfigureAwait(false);
            _core._log.LogInformation("finlight ws: proactive rotation before server connection cap");
            await TryCloseOutputAsync((WebSocketCloseStatus)4000, "Proactive rotation", token).ConfigureAwait(false);
            // Give the server a grace period to answer with its close frame,
            // then force the receive loop out of its read.
            await Task.Delay(CloseGracePeriod, _core._time, token).ConfigureAwait(false);
            if (_socket.State is not (WebSocketState.Closed or WebSocketState.Aborted))
            {
                _socket.Abort();
            }
        }

        private async Task SendJsonAsync(object value, CancellationToken cancellationToken)
        {
            var data = JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), FinlightJson.Options);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var sendCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                sendCts.CancelAfter(SendTimeout);
                await _socket.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, sendCts.Token)
                    .ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private async Task TryCloseOutputAsync(
            WebSocketCloseStatus status,
            string description,
            CancellationToken cancellationToken)
        {
            try
            {
                await _socket.CloseOutputAsync(status, description, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _core._log.LogDebug("finlight ws: close failed ({Error})", exception.Message);
            }
        }

        private void NotifyClose(int code, string reason)
        {
            if (_closeNotified)
            {
                return;
            }

            _closeNotified = true;
            var onClose = _core._wsOptions.OnClose;
            if (onClose is null)
            {
                return;
            }

            try
            {
                onClose(code, reason);
            }
            catch (Exception exception)
            {
                _core._log.LogError(exception, "finlight ws: OnClose callback threw");
            }
        }

        private static string RawToString(JsonElement? element)
            => element is not { } value
                ? ""
                : value.ValueKind == JsonValueKind.String
                    ? value.GetString() ?? ""
                    : value.GetRawText();

        public async ValueTask DisposeAsync()
        {
            await _lifetimeCts.CancelAsync().ConfigureAwait(false);
            if (_keepalive is not null)
            {
                try
                {
                    await _keepalive.ConfigureAwait(false);
                }
                catch (Exception)
                {
                    // Keepalive failures are already logged; disposal must not throw.
                }
            }

            if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    using var closeCts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await _socket
                        .CloseAsync(WebSocketCloseStatus.NormalClosure, "client stopped", closeCts.Token)
                        .ConfigureAwait(false);
                }
                catch (Exception)
                {
                    _socket.Abort();
                }

                NotifyClose((int)WebSocketCloseStatus.NormalClosure, "client stopped");
            }

            _socket.Dispose();
            _sendLock.Dispose();
            _lifetimeCts.Dispose();
        }
    }
}
