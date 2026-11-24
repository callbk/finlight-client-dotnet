using System.Buffers;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;

namespace Finlight.Internal;

/// <summary>Test-only observation points of a stream; not part of the public API.</summary>
internal sealed class StreamTestHooks
{
    public Action<string>? OnFrame { get; set; }

    public Action? OnPong { get; set; }

    public Action? OnPingSent { get; set; }

    public Action<string>? OnDuplicate { get; set; }

    public Action<TimeSpan, ReconnectReason>? OnReconnectScheduled { get; set; }

    public Action<int>? OnConnectionClosed { get; set; }
}

/// <summary>
/// The engine of one stream. A single control loop handles every signal (connects, frames,
/// closes, timers) in order, so control frames are handled apart from the consumer and a slow
/// consumer cannot trip the watchdog. Each connection has a pump task that dials and reads frames.
/// </summary>
internal sealed class StreamCore
{
    private const string NoWebSocketsPrefix = "Your subscription does not include WebSockets";
    private const string QuotaPrefix = "WebSocket quota exceeded";
    private const string TooManyPrefix = "Too many concurrent connections";
    private static readonly TimeSpan CloseGrace = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(10);

    private readonly FinlightConfig _cfg;
    private readonly ILogger _log;
    private readonly TimeProvider _time;
    private readonly WsConnector _connector;
    private readonly Uri _uri;
    private readonly bool _raw;
    private readonly JsonObject _subscribe;
    private readonly StreamOptions _options;
    private readonly Channel<StreamEvent> _out = Channel.CreateUnbounded<StreamEvent>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Signal> _signals = Channel.CreateUnbounded<Signal>(new UnboundedChannelOptions { SingleReader = true });
    private readonly HashSet<Conn> _conns = [];
    private readonly HashSet<ITimer> _timers = [];
    private readonly Queue<string> _dedupeOrder = new();
    private readonly HashSet<string> _dedupe = [];
    private readonly object _startLock = new();
    private readonly Action<StreamCore>? _onEnded;

    private Task? _loop;
    private Conn? _current;
    private Conn? _rotatingFrom;
    private ITimer? _reconnectTimer;
    private FinlightException? _pendingQuotaError;
    private DateTimeOffset? _lastSeenTime;
    private int _failures;
    private int _quotaWaits;
    private int _searchFailed;
    private int _connections;
    private volatile bool _ended;
    private int _pending;

    public StreamCore(FinlightConfig cfg, ILogger log, WsConnector connector, bool raw, JsonObject subscribe, StreamOptions options, Action<StreamCore>? onEnded = null)
    {
        _onEnded = onEnded;
        _cfg = cfg;
        _log = log;
        _time = cfg.TimeProvider;
        _connector = connector;
        _raw = raw;
        _subscribe = subscribe;
        _options = options;
        var baseUrl = cfg.WssUrl.TrimEnd('/');
        _uri = new Uri(raw ? baseUrl + "/raw" : baseUrl);
    }

    public StreamTestHooks Hooks { get; } = new();

    public ChannelReader<StreamEvent> Output => _out.Reader;

    public bool Ended => _ended;

    public Exception? Terminal { get; private set; }

    public string? LastSeenAt { get; private set; }

    /// <summary>Open timers (tests check that stopping leaves none).</summary>
    public int OpenTimers
    {
        get
        {
            lock (_timers)
            {
                return _timers.Count;
            }
        }
    }

    /// <summary>Open sockets (tests check that stopping leaves none).</summary>
    public int OpenConnections
    {
        get
        {
            lock (_conns)
            {
                return _conns.Count;
            }
        }
    }

    /// <summary>True when no signal waits and none is being handled (tests use it to settle).</summary>
    public bool Idle => Volatile.Read(ref _pending) == 0;

    public void Start()
    {
        lock (_startLock)
        {
            if (_loop is not null || _ended)
            {
                return;
            }

            _loop = Task.Run(RunAsync);
        }
    }

    public async Task StopAsync()
    {
        Task? loop;
        lock (_startLock)
        {
            loop = _loop;
            if (loop is null)
            {
                _ended = true;
                _out.Writer.TryComplete();
                _onEnded?.Invoke(this);
                return;
            }
        }

        Post(new Stop());
        await loop.ConfigureAwait(false);
    }

    /// <summary>Asks the stream to stop without waiting for it (used by <c>FinlightClient.Dispose</c>).</summary>
    public void RequestStop()
    {
        lock (_startLock)
        {
            if (_loop is null)
            {
                _ended = true;
                _out.Writer.TryComplete();
                _onEnded?.Invoke(this);
                return;
            }
        }

        Post(new Stop());
    }

    private async Task RunAsync()
    {
        StartDial(ReconnectReason.Closed, first: true);
        await foreach (var signal in _signals.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            try
            {
                await HandleAsync(signal).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                // Never reached for server input; a bug here must not leave the consumer hanging.
                _log.LogError(exception, "stream: unexpected failure; the stream ends");
                await FinishAsync(new FinlightNetworkException("the stream failed unexpectedly", exception)).ConfigureAwait(false);
            }

            finally
            {
                Interlocked.Decrement(ref _pending);
            }

            if (_ended)
            {
                break;
            }
        }
    }

    private void Post(Signal signal)
    {
        Interlocked.Increment(ref _pending);
        if (!_signals.Writer.TryWrite(signal))
        {
            Interlocked.Decrement(ref _pending);
        }
    }

    private async Task HandleAsync(Signal signal)
    {
        switch (signal)
        {
            case Stop:
                await FinishAsync(null).ConfigureAwait(false);
                break;
            case Opened opened:
                await OnOpenedAsync(opened).ConfigureAwait(false);
                break;
            case DialFailed failed:
                failed.Conn.Refusal = failed.Status is { } status ? new Refusal(status, failed.Body, failed.Error) : null;
                failed.Conn.DialError = failed.Error;
                failed.Conn.DialTimedOut = failed.TimedOut;
                await OnClosedAsync(failed.Conn, null).ConfigureAwait(false);
                break;
            case FrameReceived frame:
                await OnFrameAsync(frame.Conn, frame.Text).ConfigureAwait(false);
                break;
            case ConnClosed closed:
                await OnClosedAsync(closed.Conn, closed.CloseStatus).ConfigureAwait(false);
                break;
            case PingDue ping:
                await OnPingAsync(ping.Conn).ConfigureAwait(false);
                break;
            case WatchdogFired watchdog:
                if (Contains(watchdog.Conn) && watchdog.Conn.Planned is null)
                {
                    _log.LogWarning("stream: no pong for {Timeout}; reconnecting", _cfg.WatchdogTimeout);
                    Drop(watchdog.Conn, Planned.Reconnect(ReconnectReason.Watchdog, DelayKind.Now), graceful: false);
                }

                break;
            case RotationDue rotation:
                if (rotation.Conn == _current && rotation.Conn.Planned is null)
                {
                    _log.LogInformation("stream: rotating the connection before the 2 h gateway limit");
                    Hooks.OnReconnectScheduled?.Invoke(TimeSpan.Zero, ReconnectReason.Rotation);
                    StartDial(ReconnectReason.Rotation, first: false);
                }

                break;
            case ReconnectDue due:
                DisposeTimer(ref _reconnectTimer);
                StartDial(due.Reason, first: false);
                break;
        }
    }

    // ---- connections -------------------------------------------------------------------

    private void StartDial(ReconnectReason reason, bool first)
    {
        if (_ended)
        {
            return;
        }

        _connections++;
        var conn = new Conn(_connections);
        lock (_conns)
        {
            _conns.Add(conn);
        }

        if (_current is not null)
        {
            _rotatingFrom = _current;
        }

        _current = conn;
        conn.Pump = Task.Run(() => PumpAsync(conn, reason, first));
    }

    private async Task PumpAsync(Conn conn, ReconnectReason reason, bool first)
    {
        var socket = conn.Socket;
        // The server answers application pings only; protocol-level keep-alive stays off.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.CollectHttpResponseDetails = true;
        // Header names go out exactly like this: the gateway reads x-api-key case-sensitively.
        socket.Options.SetRequestHeader("x-api-key", _cfg.ApiKey);
        socket.Options.SetRequestHeader("x-client-version", ClientInfo.Version);
        socket.Options.SetRequestHeader("User-Agent", ClientInfo.UserAgent);
        if (_options.Takeover)
        {
            socket.Options.SetRequestHeader("x-takeover", "true");
        }

        HttpMessageInvoker? invoker = null;
        RefusalCapture? capture = null;
        using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(conn.Cts.Token);
        try
        {
            (invoker, capture) = _connector.Create();
            connectCts.CancelAfter(_cfg.ConnectTimeout);
            await socket.ConnectAsync(_uri, invoker, connectCts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            var status = capture?.Status ?? (socket.HttpStatusCode != 0 ? (int)socket.HttpStatusCode : (int?)null);
            var timedOut = connectCts.IsCancellationRequested && !conn.Cts.IsCancellationRequested;
            Post(new DialFailed(conn, status, capture?.Body, exception, timedOut));
            return;
        }
        finally
        {
            invoker?.Dispose();
        }

        Post(new Opened(conn, reason, first));
        int? closeStatus = null;
        try
        {
            while (true)
            {
                var text = await ReceiveAsync(socket, conn.Cts.Token).ConfigureAwait(false);
                if (text is null)
                {
                    closeStatus = (int?)socket.CloseStatus;
                    await CloseOutputAsync(conn).ConfigureAwait(false);
                    break;
                }

                Post(new FrameReceived(conn, text));
            }
        }
        catch (Exception exception)
        {
            _log.LogDebug("stream: connection {Id} ended ({Error})", conn.Id, exception.GetType().Name);
            closeStatus = (int?)socket.CloseStatus;
        }

        Post(new ConnClosed(conn, closeStatus));
    }

    private async Task<string?> ReceiveAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            using var message = new MemoryStream();
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return null;
                }

                if (message.Length + result.Count > _cfg.MaxMessageBytes)
                {
                    socket.Abort();
                    throw new InvalidDataException($"a stream message is larger than MaxMessageBytes ({_cfg.MaxMessageBytes})");
                }

                message.Write(buffer, 0, result.Count);
                if (result.EndOfMessage)
                {
                    return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task OnOpenedAsync(Opened opened)
    {
        var conn = opened.Conn;
        if (_ended || !Contains(conn))
        {
            return;
        }

        conn.Opened = true;
        // The frame is the checked, fixed-key subscribe plus this SDK's clientNonce.
        var frame = (JsonObject)_subscribe.DeepClone();
        frame["clientNonce"] = _cfg.NonceFactory?.Invoke() ?? "c-" + Guid.NewGuid().ToString("N");
        await SendAsync(conn, frame.ToJsonString(FinlightJson.Options)).ConfigureAwait(false);
        conn.Ping = CreateTimer(() => Post(new PingDue(conn)), _cfg.PingInterval, _cfg.PingInterval);
        conn.Watchdog = CreateTimer(() => Post(new WatchdogFired(conn)), _cfg.WatchdogTimeout, Timeout.InfiniteTimeSpan);
        conn.Rotation = CreateTimer(() => Post(new RotationDue(conn)), _cfg.RotationInterval, Timeout.InfiniteTimeSpan);
        if (!opened.First)
        {
            CallUser("OnReconnect", _options.OnReconnect, new ReconnectInfo(LastSeenAt, opened.Reason, conn.Id));
        }
    }

    private async Task OnPingAsync(Conn conn)
    {
        if (!Contains(conn) || conn.Planned is not null || conn.Socket.State != WebSocketState.Open)
        {
            return;
        }

        var ping = new JsonObject { ["action"] = "ping", ["t"] = _time.GetUtcNow().ToUnixTimeMilliseconds() };
        await SendAsync(conn, ping.ToJsonString()).ConfigureAwait(false);
        Hooks.OnPingSent?.Invoke();
    }

    private async Task OnClosedAsync(Conn conn, int? closeStatus)
    {
        if (conn.Handled)
        {
            return;
        }

        conn.Handled = true;
        DisposeTimers(conn);
        lock (_conns)
        {
            _conns.Remove(conn);
        }

        conn.Socket.Dispose();
        Hooks.OnConnectionClosed?.Invoke(conn.Id);
        if (_ended)
        {
            return;
        }

        if (conn == _rotatingFrom)
        {
            _rotatingFrom = null;
            return;
        }

        if (conn != _current)
        {
            return;
        }

        _current = null;
        if (_rotatingFrom is { } rotatingFrom && conn.Planned is null && !conn.Opened && !DialFailureEndsStream(conn))
        {
            // The dial of a rotation was refused or failed while the old connection still works. On a
            // plan at its connection limit (often a limit of 1) the old connection holds the slot, so a
            // refusal is expected and not an error: close the old connection, wait for its close and
            // dial again at once. Only when that dial fails too does the usual wait apply.
            _log.LogInformation("stream: the rotation dial failed; closing the old connection and dialing again");
            _rotatingFrom = null;
            _current = rotatingFrom;
            Drop(rotatingFrom, Planned.Reconnect(ReconnectReason.Rotation, DelayKind.Now), graceful: true);
            return;
        }

        var planned = conn.Planned ?? Classify(conn, closeStatus);
        if (_rotatingFrom is { } old)
        {
            // The new connection of a rotation failed: keep nothing half-open, reconnect now.
            _rotatingFrom = null;
            Drop(old, Planned.Reconnect(ReconnectReason.Rotation, DelayKind.Now), graceful: true);
            if (planned.Error is null)
            {
                planned = Planned.Reconnect(ReconnectReason.Rotation, DelayKind.Now);
            }
        }

        if (planned.Error is { } terminal)
        {
            await FinishAsync(terminal).ConfigureAwait(false);
            return;
        }

        ScheduleReconnect(planned.Reason, planned.Delay);
    }

    private Planned Classify(Conn conn, int? closeStatus)
    {
        if (conn.Refusal is { } refusal)
        {
            var status = refusal.Status;
            var message = RefusalMessage(refusal.Body);
            var text = message ?? string.Empty;
            if (status == 401)
            {
                return Planned.Terminal(DialError(refusal, message));
            }

            if (status == 403)
            {
                if (text.StartsWith(NoWebSocketsPrefix, StringComparison.Ordinal))
                {
                    return Planned.Terminal(DialError(refusal, message));
                }

                if (text.StartsWith(QuotaPrefix, StringComparison.Ordinal))
                {
                    return QuotaPlan(DialError(refusal, message));
                }

                if (text.StartsWith(TooManyPrefix, StringComparison.Ordinal))
                {
                    Report(DialError(
                        refusal,
                        message,
                        text + " Your connection limit is shared by enriched, raw and older SDK streams; the SDK retries after a long wait."
                            + " Set StreamOptions.Takeover = true to replace your stalest connection (takeover)."));
                    return Planned.Reconnect(ReconnectReason.ConnectionLimit, DelayKind.Long);
                }

                // A 403 without a readable body: long wait plus an error report (R-WS-2).
                Report(DialError(refusal, message));
                return Planned.Reconnect(ReconnectReason.DialRefused, DelayKind.Long);
            }

            if (status == 429)
            {
                Report(DialError(refusal, message));
                return Planned.Reconnect(ReconnectReason.RateLimited, DelayKind.Long);
            }

            if (status >= 500)
            {
                return Planned.Reconnect(ReconnectReason.DialRefused, DelayKind.Backoff);
            }

            Report(DialError(refusal, message));
            return Planned.Reconnect(ReconnectReason.DialRefused, DelayKind.Backoff);
        }

        if (closeStatus == (int)WebSocketCloseStatus.PolicyViolation)
        {
            return Planned.Terminal(FinlightApiException.Create(
                new ApiErrorData
                {
                    Status = 1008,
                    Title = "Policy violation",
                    Detail = "the server closed the stream with code 1008",
                    Message = "the server closed the stream with code 1008 (policy violation)",
                },
                kindFromStatus: false));
        }

        if (!conn.Opened)
        {
            if (conn.DialError is ObjectDisposedException || _connector.Disposed)
            {
                // The FinlightClient was disposed: redialing can never work again.
                return Planned.Terminal(new FinlightNetworkException(
                    "the FinlightClient was disposed; the stream ends", conn.DialError));
            }

            Report(conn.DialTimedOut
                ? new FinlightTimeoutException(
                    $"could not connect to the stream endpoint {_uri.Host} within {_cfg.ConnectTimeout}", conn.DialError)
                : new FinlightNetworkException(
                    $"could not connect to the stream endpoint {_uri.Host}" + (conn.DialError is { } dialError ? $" ({dialError.Message})" : string.Empty),
                    conn.DialError));
            return Planned.Reconnect(ReconnectReason.DialFailed, DelayKind.Backoff);
        }

        return Planned.Reconnect(ReconnectReason.Closed, DelayKind.Backoff);
    }

    /// <summary>Exhausted quota: wait and retry hourly (default), or end the stream when switched off.</summary>
    private Planned QuotaPlan(FinlightException error)
    {
        if (!_cfg.QuotaReconnect)
        {
            return Planned.Terminal(error);
        }

        _pendingQuotaError = error;
        return Planned.Reconnect(ReconnectReason.QuotaExceeded, DelayKind.Quota);
    }

    private void ScheduleReconnect(ReconnectReason reason, DelayKind kind)
    {
        var delay = kind switch
        {
            DelayKind.Backoff => Backoff(),
            DelayKind.Long => _cfg.LongBackoff + TimeSpan.FromMilliseconds(Math.Round(_cfg.Random() * 0.2 * _cfg.LongBackoff.TotalMilliseconds)),
            DelayKind.Quota => _cfg.QuotaReconnectInterval,
            _ => TimeSpan.Zero,
        };
        if (kind == DelayKind.Quota)
        {
            _quotaWaits++;
            var error = _pendingQuotaError ?? DialError(new Refusal(403, null), QuotaPrefix);
            _pendingQuotaError = null;
            _log.LogWarning(
                "stream: WebSocket quota exhausted ({Error}); attempt {Attempt}, next connect in {Delay}",
                error.Message, _quotaWaits, delay);
            CallUser("OnQuotaWait", _options.OnQuotaWait, new QuotaWaitInfo(error, delay, _quotaWaits));
        }

        _failures++;
        _log.LogDebug("stream: reconnect ({Reason}) in {Delay}", reason.Value, delay);
        Hooks.OnReconnectScheduled?.Invoke(delay, reason);
        if (delay <= TimeSpan.Zero)
        {
            StartDial(reason, first: false);
            return;
        }

        _reconnectTimer = CreateTimer(() => Post(new ReconnectDue(reason)), delay, Timeout.InfiniteTimeSpan);
    }

    private TimeSpan Backoff()
    {
        var baseMs = _cfg.ReconnectBaseDelay.TotalMilliseconds * Math.Pow(2, Math.Min(_failures, 16));
        var capped = Math.Min(_cfg.ReconnectMaxDelay.TotalMilliseconds, baseMs);
        return TimeSpan.FromMilliseconds(Math.Max(1, Math.Round(capped * (0.5 + (_cfg.Random() * 0.5)))));
    }

    /// <summary>Closes a connection with a decided follow-up; the pump then reports the close.</summary>
    private void Drop(Conn conn, Planned planned, bool graceful)
    {
        conn.Planned ??= planned;
        DisposeTimers(conn);
        if (graceful)
        {
            _ = CloseGracefullyAsync(conn);
        }
        else
        {
            conn.Socket.Abort();
        }
    }

    private async Task CloseGracefullyAsync(Conn conn)
    {
        await CloseOutputAsync(conn).ConfigureAwait(false);
        var pump = conn.Pump;
        if (pump is not null && await Task.WhenAny(pump, Task.Delay(CloseGrace)).ConfigureAwait(false) != pump)
        {
            conn.Socket.Abort();
        }
    }

    private async Task CloseOutputAsync(Conn conn)
    {
        await conn.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (conn.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var cts = new CancellationTokenSource(CloseGrace);
                await conn.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closed", cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception exception)
        {
            _log.LogDebug("stream: close of connection {Id} failed ({Error})", conn.Id, exception.GetType().Name);
            conn.Socket.Abort();
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    private async Task SendAsync(Conn conn, string text)
    {
        await conn.SendLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (conn.Socket.State != WebSocketState.Open)
            {
                return;
            }

            using var cts = new CancellationTokenSource(SendTimeout);
            await conn.Socket.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, cts.Token).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            // The pump sees the broken connection and reports the close.
            _log.LogDebug("stream: send on connection {Id} failed ({Error})", conn.Id, exception.GetType().Name);
        }
        finally
        {
            conn.SendLock.Release();
        }
    }

    /// <summary>Ends the stream: closes every socket with 1000, stops every timer, completes the output.</summary>
    private async Task FinishAsync(Exception? terminal)
    {
        if (_ended)
        {
            return;
        }

        _ended = true;
        Terminal = terminal;
        DisposeTimer(ref _reconnectTimer);
        List<Conn> conns;
        lock (_conns)
        {
            conns = [.. _conns];
        }

        foreach (var conn in conns)
        {
            DisposeTimers(conn);
            conn.Planned ??= Planned.Reconnect(ReconnectReason.Closed, DelayKind.Now);
            if (conn.Socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                // Close with 1000 and let the pump read the server's close frame.
                await CloseOutputAsync(conn).ConfigureAwait(false);
            }
            else
            {
                // Still connecting (or already gone): cancel the dial.
                conn.Cts.Cancel();
            }

            if (conn.Pump is { } pump)
            {
                if (await Task.WhenAny(pump, Task.Delay(CloseGrace)).ConfigureAwait(false) != pump)
                {
                    conn.Cts.Cancel();
                    conn.Socket.Abort();
                }

                try
                {
                    await pump.ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    _log.LogDebug("stream: pump of connection {Id} failed ({Error})", conn.Id, exception.GetType().Name);
                }
            }

            conn.Socket.Dispose();
        }

        lock (_conns)
        {
            _conns.Clear();
        }

        _current = null;
        _rotatingFrom = null;
        _dedupe.Clear();
        _dedupeOrder.Clear();
        _out.Writer.TryComplete();
        _onEnded?.Invoke(this);
    }

    // ---- frames ------------------------------------------------------------------------

    private async Task OnFrameAsync(Conn conn, string text)
    {
        if (_ended || !Contains(conn))
        {
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            _log.LogWarning("stream: ignored a frame that is not JSON");
            Hooks.OnFrame?.Invoke(text);
            return;
        }

        using (document)
        {
            var frame = Frames.Decode(document.RootElement);
            await HandleFrameAsync(conn, frame, text).ConfigureAwait(false);
        }

        Hooks.OnFrame?.Invoke(text);
    }

    private async Task HandleFrameAsync(Conn conn, DecodedFrame frame, string text)
    {
        switch (frame.Type)
        {
            case FrameType.Pong:
                conn.Watchdog?.Change(_cfg.WatchdogTimeout, Timeout.InfiniteTimeSpan);
                // The connection works: the reconnect backoff starts over.
                _failures = 0;
                Hooks.OnPong?.Invoke();
                break;
            case FrameType.Admitted:
                // Admitted comes before the subscribe is checked and the initial search runs, so it
                // does not reset the reconnect backoff; a pong or an article does.
                _quotaWaits = 0;
                if (conn == _current && _rotatingFrom is { } old)
                {
                    _rotatingFrom = null;
                    Drop(old, Planned.Reconnect(ReconnectReason.Rotation, DelayKind.Now), graceful: true);
                }

                CallUser("OnAdmitted", _options.OnAdmitted, new AdmittedInfo(frame.LeaseId, frame.ServerNow, frame.ClientNonce));
                break;
            case FrameType.Preempted:
                if (conn != _current)
                {
                    // A connection this stream is already retiring, typically the old connection of a
                    // rotation with takeover at the connection limit: the server preempts it in favour of
                    // our own new connection. That is the planned end of the old connection, not of the
                    // stream. Only a preempt of the current connection is terminal (R-WS-2, R-WS-4).
                    _log.LogDebug("stream: old connection {Id} preempted (reason {Reason}) during rotation", conn.Id, frame.Reason);
                    Drop(conn, Planned.Reconnect(ReconnectReason.Rotation, DelayKind.Now), graceful: true);
                    break;
                }

                var preempted = new PreemptedException(new PreemptReason(frame.Reason!), frame.NewLeaseId, frame.Timestamp);
                if (frame.Reason == PreemptReasonValues.QuotaExceeded)
                {
                    var plan = QuotaPlan(preempted);
                    if (plan.Error is null)
                    {
                        Drop(conn, plan, graceful: true);
                        break;
                    }
                }

                _log.LogWarning("stream: preempted by the server (reason {Reason}); the stream ends", frame.Reason);
                await FinishAsync(preempted).ConfigureAwait(false);
                break;
            case FrameType.Error:
                await HandleErrorAsync(conn, frame).ConfigureAwait(false);
                break;
            case FrameType.Article:
                HandleArticle(frame, text);
                break;
            case FrameType.Other:
                _log.LogDebug("stream: unknown event {Event}, passed on as other", frame.Event);
                _out.Writer.TryWrite(new StreamEvent(StreamEventKind.Other, new StreamEventName(frame.Event!), null, text));
                break;
            default:
                _log.LogDebug("stream: ignored a frame: {Reason}", frame.Ignored);
                break;
        }
    }

    private async Task HandleErrorAsync(Conn conn, DecodedFrame frame)
    {
        var error = StreamError(frame);
        switch (frame.Code)
        {
            case ConnectionErrorCodeValues.ValidationFailed:
                _log.LogWarning("stream: the subscribe was rejected ({Message}); the stream ends", frame.Message);
                await FinishAsync(error).ConfigureAwait(false);
                break;
            case ConnectionErrorCodeValues.HandshakeFailed:
                _log.LogWarning("stream: handshake failed; reconnecting");
                Drop(conn, Planned.Reconnect(ReconnectReason.HandshakeFailed, DelayKind.Backoff), graceful: true);
                break;
            case ConnectionErrorCodeValues.SearchFailed:
                _searchFailed++;
                if (_searchFailed >= _cfg.SearchFailedThreshold)
                {
                    _searchFailed = 0;
                    Report(error);
                }
                else
                {
                    _log.LogWarning("stream: the initial search failed; reconnecting");
                }

                Drop(conn, Planned.Reconnect(ReconnectReason.SearchFailed, DelayKind.Backoff), graceful: true);
                break;
            default:
                // PING_ON_DEFAULT_ROUTE, INTERNAL_ERROR, unknown codes and v2-form errors: report only.
                Report(error);
                break;
        }
    }

    private void HandleArticle(DecodedFrame frame, string text)
    {
        _searchFailed = 0;
        _failures = 0;
        Article article;
        try
        {
            article = Decode.Article(frame.Data, "$.data");
        }
        catch (FinlightDecodeException exception)
        {
            Report(new FinlightDecodeException(
                $"skipped a stream article that could not be decoded: {exception.Message}", exception.Path, exception.Raw, exception));
            return;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frame.Data.GetRawText())));
        var key = $"{frame.Event}\n{article.Id}\n{hash}";
        if (_dedupe.Contains(key))
        {
            _log.LogDebug("stream: dropped a duplicate {Event} for {Id}", frame.Event, article.Id);
            Hooks.OnDuplicate?.Invoke(article.Id);
            return;
        }

        _dedupe.Add(key);
        _dedupeOrder.Enqueue(key);
        while (_dedupeOrder.Count > Math.Max(1, _cfg.DedupeCacheSize))
        {
            _dedupe.Remove(_dedupeOrder.Dequeue());
        }

        var seen = _raw ? article.FirstSeenAt : article.AvailableAt ?? article.FirstSeenAt;
        if (seen is not null && Rfc3339.Parse(seen) is { } seenTime && (_lastSeenTime is null || seenTime >= _lastSeenTime))
        {
            _lastSeenTime = seenTime;
            LastSeenAt = seen;
        }

        _out.Writer.TryWrite(new StreamEvent(frame.Kind, new StreamEventName(frame.Event!), article, text));
    }

    private static FinlightApiException StreamError(DecodedFrame frame)
    {
        var issues = new List<FieldIssue>();
        if (frame.Details.ValueKind == JsonValueKind.Array)
        {
            foreach (var detail in frame.Details.EnumerateArray())
            {
                if (detail.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var constraints = new Dictionary<string, string>();
                if (detail.TryGetProperty("constraints", out var c) && c.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in c.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.String)
                        {
                            constraints[property.Name] = property.Value.GetString()!;
                        }
                    }
                }

                issues.Add(new FieldIssue
                {
                    Field = ErrorParser.Str(detail, "field"),
                    Issue = constraints.Count > 0 ? string.Join("; ", constraints.Values) : null,
                    Constraints = constraints,
                });
            }
        }

        return FinlightApiException.Create(
            new ApiErrorData
            {
                Status = 0,
                Code = frame.Code,
                Title = "Stream error",
                Detail = frame.Message,
                Errors = issues,
                Message = $"stream error {frame.Code ?? "(no code)"}: {frame.Message}".TrimEnd(),
            },
            kindFromStatus: false);
    }

    private FinlightApiException DialError(Refusal refusal, string? message, string? detail = null)
        => FinlightApiException.Create(
            new ApiErrorData
            {
                Cause = refusal.Cause,
                Status = refusal.Status,
                Title = "WebSocket connection refused",
                Detail = detail ?? message,
                Raw = refusal.Body,
                LimitType = refusal.Status == 429 ? RateLimitType.Unknown : (RateLimitType?)null,
                Message = $"WebSocket connection refused with HTTP {refusal.Status}" + (message is null ? string.Empty : $": {message}"),
            },
            kindFromStatus: true);

    /// <summary>True for a dial failure that ends the stream (as <see cref="Classify"/> decides), without side effects.</summary>
    private bool DialFailureEndsStream(Conn conn)
    {
        if (conn.Refusal is not { } refusal)
        {
            return conn.DialError is ObjectDisposedException || _connector.Disposed;
        }

        var text = RefusalMessage(refusal.Body) ?? string.Empty;
        return refusal.Status == 401
            || (refusal.Status == 403 && text.StartsWith(NoWebSocketsPrefix, StringComparison.Ordinal))
            || (refusal.Status == 403 && text.StartsWith(QuotaPrefix, StringComparison.Ordinal) && !_cfg.QuotaReconnect);
    }

    private static string? RefusalMessage(string? body)
    {
        if (string.IsNullOrEmpty(body))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            return ErrorParser.Str(doc.RootElement, "message") ?? ErrorParser.Str(doc.RootElement, "error");
        }
        catch (JsonException)
        {
            return null;
        }
    }

    // ---- helpers -----------------------------------------------------------------------

    private void Report(FinlightException error)
    {
        _log.LogWarning("stream: {Error}", error.Message);
        CallUser("OnError", _options.OnError, error);
    }

    private void CallUser<T>(string name, Action<T>? hook, T argument)
    {
        if (hook is null)
        {
            return;
        }

        try
        {
            hook(argument);
        }
        catch (Exception exception)
        {
            _log.LogError(exception, "stream: the {Hook} hook threw; the stream continues", name);
        }
    }

    private bool Contains(Conn conn)
    {
        lock (_conns)
        {
            return _conns.Contains(conn);
        }
    }

    private ITimer CreateTimer(Action action, TimeSpan due, TimeSpan period)
    {
        var timer = _time.CreateTimer(_ => action(), null, due, period);
        lock (_timers)
        {
            _timers.Add(timer);
        }

        return timer;
    }

    private void DisposeTimer(ref ITimer? timer)
    {
        if (timer is null)
        {
            return;
        }

        timer.Dispose();
        lock (_timers)
        {
            _timers.Remove(timer);
        }

        timer = null;
    }

    private void DisposeTimers(Conn conn)
    {
        DisposeTimer(ref conn.Ping);
        DisposeTimer(ref conn.Watchdog);
        DisposeTimer(ref conn.Rotation);
    }

    private enum DelayKind
    {
        Now,
        Backoff,
        Long,
        Quota,
    }

    private sealed record Planned(ReconnectReason Reason, DelayKind Delay, Exception? Error)
    {
        public static Planned Reconnect(ReconnectReason reason, DelayKind delay) => new(reason, delay, null);

        public static Planned Terminal(Exception error) => new(ReconnectReason.Closed, DelayKind.Now, error);
    }

    private sealed record Refusal(int Status, string? Body, Exception? Cause = null);

    private sealed class Conn(int id)
    {
        public ITimer? Ping;
        public ITimer? Watchdog;
        public ITimer? Rotation;

        public int Id { get; } = id;

        public ClientWebSocket Socket { get; } = new();

        public CancellationTokenSource Cts { get; } = new();

        public SemaphoreSlim SendLock { get; } = new(1, 1);

        public Task? Pump { get; set; }

        public bool Opened { get; set; }

        public bool Handled { get; set; }

        public Planned? Planned { get; set; }

        public Refusal? Refusal { get; set; }

        public Exception? DialError { get; set; }

        public bool DialTimedOut { get; set; }
    }

    private abstract record Signal;

    private sealed record Stop : Signal;

    private sealed record Opened(Conn Conn, ReconnectReason Reason, bool First) : Signal;

    private sealed record DialFailed(Conn Conn, int? Status, string? Body, Exception Error, bool TimedOut) : Signal;

    private sealed record FrameReceived(Conn Conn, string Text) : Signal;

    private sealed record ConnClosed(Conn Conn, int? CloseStatus) : Signal;

    private sealed record PingDue(Conn Conn) : Signal;

    private sealed record WatchdogFired(Conn Conn) : Signal;

    private sealed record RotationDue(Conn Conn) : Signal;

    private sealed record ReconnectDue(ReconnectReason Reason) : Signal;
}

/// <summary>Creates the HTTP invoker of each WebSocket handshake and keeps the body of a refused one (R-WS-2).</summary>
internal sealed class WsConnector : IDisposable
{
    private readonly HttpClient? _userClient;
    private readonly HttpMessageInvoker? _shared;
    private int _disposed;

    public WsConnector(HttpClient? userClient)
    {
        _userClient = userClient;
        if (userClient is null)
        {
            _shared = new HttpMessageInvoker(
                // No redirects: SocketsHttpHandler would send the x-api-key header to the new host.
                new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5), UseCookies = false, AllowAutoRedirect = false },
                disposeHandler: true);
        }
    }

    public bool Disposed => Volatile.Read(ref _disposed) != 0;

    public (HttpMessageInvoker Invoker, RefusalCapture Capture) Create()
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        var capture = new RefusalCapture(_userClient, _shared);
        return (new HttpMessageInvoker(capture, disposeHandler: false), capture);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _shared?.Dispose();
        }
    }
}

/// <summary>Forwards the handshake and reads the body of a non-101 answer.</summary>
internal sealed class RefusalCapture(HttpClient? userClient, HttpMessageInvoker? shared) : HttpMessageHandler
{
    public int? Status { get; private set; }

    public string? Body { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = userClient is not null
            ? await userClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false)
            : await shared!.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (response.StatusCode != HttpStatusCode.SwitchingProtocols)
        {
            Status = (int)response.StatusCode;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(TimeSpan.FromSeconds(5));
                var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
                var buffer = new byte[64 * 1024];
                var total = 0;
                while (total < buffer.Length)
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total), cts.Token).ConfigureAwait(false);
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                Body = Encoding.UTF8.GetString(buffer, 0, total);
            }
            catch (Exception)
            {
                // No readable body: the status alone decides (R-WS-2 fallback).
                Body = null;
            }
        }

        return response;
    }
}
