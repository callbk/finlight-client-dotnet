using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Finlight.Internal;

namespace Finlight;

/// <summary>One item of a stream.</summary>
public sealed record StreamEvent
{
    internal StreamEvent(StreamEventKind kind, StreamEventName @event, Article? data, string rawFrame)
    {
        Kind = kind;
        Event = @event;
        Data = data;
        RawFrame = rawFrame;
    }

    /// <summary><c>created</c>, <c>updated</c>, or <c>other</c> for an event name this SDK does not know.</summary>
    public StreamEventKind Kind { get; }

    /// <summary>The wire <c>event</c> name, kept as sent.</summary>
    public StreamEventName Event { get; }

    /// <summary>The article; null for <c>other</c>.</summary>
    public Article? Data { get; }

    private string RawFrame { get; }

    /// <summary>The whole frame exactly as received.</summary>
    /// <returns>The JSON text.</returns>
    public string GetRawJson() => RawFrame;
}

/// <summary>Sent with every <c>connection.admitted</c>. It arrives before the subscribe is checked: it does not mean the subscribe was accepted.</summary>
/// <param name="LeaseId">The lease of this connection.</param>
/// <param name="ServerNow">Server time, epoch milliseconds.</param>
/// <param name="ClientNonce">The nonce this SDK sent in the subscribe.</param>
public sealed record AdmittedInfo(string? LeaseId, long? ServerNow, string? ClientNonce);

/// <summary>Sent after every new connection except the first. The SDK does not backfill: use <see cref="LastSeenAt"/> with REST.</summary>
/// <param name="LastSeenAt">Last timestamp seen on this stream (enriched <c>availableAt</c>, else <c>firstSeenAt</c>; raw <c>firstSeenAt</c>); null when nothing arrived yet.</param>
/// <param name="Reason">Why the stream reconnected.</param>
/// <param name="Connection">Connections opened by this stream so far, the new one included.</param>
public sealed record ReconnectInfo(string? LastSeenAt, ReconnectReason Reason, int Connection);

/// <summary>Sent for every wait on an exhausted WebSocket quota (<see cref="FinlightConfig.QuotaReconnect"/>).</summary>
/// <param name="Error">The <see cref="PreemptedException"/> or the refused connect (<see cref="ForbiddenException"/>).</param>
/// <param name="NextAttemptIn">Time until the next connect.</param>
/// <param name="Attempt">Consecutive quota waits, from 1.</param>
public sealed record QuotaWaitInfo(FinlightException Error, TimeSpan NextAttemptIn, int Attempt);

/// <summary>Options of one stream. Exceptions thrown by a hook are logged and never end the stream.</summary>
public sealed record StreamOptions
{
    /// <summary>Sends <c>x-takeover: true</c>: at the connection limit, replace your stalest connection. Default off.</summary>
    public bool Takeover { get; init; }

    /// <summary>Called for every <c>connection.admitted</c>.</summary>
    public Action<AdmittedInfo>? OnAdmitted { get; init; }

    /// <summary>Called after every new connection except the first.</summary>
    public Action<ReconnectInfo>? OnReconnect { get; init; }

    /// <summary>Called for problems that do not end the stream (log-only errors, repeated failures, refused connects).</summary>
    public Action<FinlightException>? OnError { get; init; }

    /// <summary>Called for every wait on an exhausted WebSocket quota (not also passed to <see cref="OnError"/>).</summary>
    public Action<QuotaWaitInfo>? OnQuotaWait { get; init; }
}

/// <summary>
/// A live stream. Iterate it once with <c>await foreach</c>. Leaving the loop, cancelling the token
/// or <see cref="DisposeAsync"/> closes the socket with 1000 and stops every timer. Terminal problems
/// (refused key, a plan without WebSockets, a rejected subscribe, a <c>connection_limit</c> preempt)
/// end the iteration with a typed exception. Reconnects, rotation and duplicate filtering run inside.
/// </summary>
public sealed class FinlightStream : IAsyncEnumerable<StreamEvent>, IAsyncDisposable
{
    private readonly StreamCore _core;
    private readonly CancellationToken _callToken;
    private int _used;

    internal FinlightStream(StreamCore core, CancellationToken callToken)
    {
        _core = core;
        _callToken = callToken;
    }

    /// <summary>The last timestamp seen (see <see cref="ReconnectInfo.LastSeenAt"/>).</summary>
    public string? LastSeenAt => _core.LastSeenAt;

    /// <summary>True once the stream has ended.</summary>
    public bool Closed => _core.Ended;

    internal StreamCore Core => _core;

    /// <inheritdoc />
    public IAsyncEnumerator<StreamEvent> GetAsyncEnumerator(CancellationToken cancellationToken = default)
    {
        if (Interlocked.Exchange(ref _used, 1) != 0)
        {
            throw new FinlightValidationException("a stream can be iterated only once; call StreamAsync again for a new one");
        }

        return IterateAsync(cancellationToken).GetAsyncEnumerator(cancellationToken);
    }

    /// <summary>Stops the stream: closes sockets with 1000 and stops every timer.</summary>
    /// <returns>A task that completes when everything is closed.</returns>
    public Task CloseAsync() => _core.StopAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync() => await _core.StopAsync().ConfigureAwait(false);

    private async IAsyncEnumerable<StreamEvent> IterateAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_callToken, cancellationToken);
        var token = linked.Token;
        _core.Start();
        try
        {
            var reader = _core.Output;
            while (await WaitAsync(reader, token, cancellationToken).ConfigureAwait(false))
            {
                while (reader.TryRead(out var item))
                {
                    yield return item;
                }
            }

            if (_core.Terminal is { } terminal)
            {
                throw terminal;
            }
        }
        finally
        {
            await _core.StopAsync().ConfigureAwait(false);
        }
    }

    private async Task<bool> WaitAsync(ChannelReader<StreamEvent> reader, CancellationToken token, CancellationToken enumeratorToken)
    {
        try
        {
            return await reader.WaitToReadAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException canceled) when (token.IsCancellationRequested)
        {
            await _core.StopAsync().ConfigureAwait(false);

            // The caller's token, not the internal linked one (R-RETRY-1).
            var caller = _callToken.IsCancellationRequested ? _callToken : enumeratorToken;
            throw new OperationCanceledException(canceled.Message, canceled, caller);
        }
    }
}
