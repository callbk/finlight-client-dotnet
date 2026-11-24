using System.Text.Json;
using System.Text.Json.Nodes;
using Finlight.Internal;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests.Support;

/// <summary>Runs one stream against a <see cref="WsMock"/> on a fake clock and records everything it does.</summary>
internal sealed class StreamProbe : IAsyncDisposable
{
    private readonly object _lock = new();
    private readonly List<StreamEvent> _items = [];
    private readonly List<AdmittedInfo> _admitted = [];
    private readonly List<ReconnectInfo> _reconnects = [];
    private readonly List<FinlightException> _errors = [];
    private readonly List<QuotaWaitInfo> _quotaWaits = [];
    private readonly List<(TimeSpan Delay, ReconnectReason Reason, DateTimeOffset At)> _scheduled = [];
    private readonly List<string> _duplicates = [];
    private readonly WsMock _ws;
    private int _frames;
    private int _pongs;
    private int _pingsSent;
    private Task? _pump;

    public StreamProbe(WsMock ws) => _ws = ws;

    public FakeTimeProvider Clock { get; } = new(new DateTimeOffset(2026, 11, 24, 0, 0, 0, TimeSpan.Zero));

    public FinlightClient Client { get; private set; } = null!;

    public FinlightStream Stream { get; private set; } = null!;

    public FinlightConfig Config { get; private set; } = null!;

    public Exception? Terminal { get; private set; }

    public volatile bool Done;

    public int Frames => Volatile.Read(ref _frames);

    public int Pongs => Volatile.Read(ref _pongs);

    public int PingsSent => Volatile.Read(ref _pingsSent);

    public IReadOnlyList<StreamEvent> Items => Snapshot(_items);

    public IReadOnlyList<AdmittedInfo> Admitted => Snapshot(_admitted);

    public IReadOnlyList<ReconnectInfo> Reconnects => Snapshot(_reconnects);

    public IReadOnlyList<FinlightException> Errors => Snapshot(_errors);

    public IReadOnlyList<QuotaWaitInfo> QuotaWaits => Snapshot(_quotaWaits);

    public IReadOnlyList<(TimeSpan Delay, ReconnectReason Reason, DateTimeOffset At)> Scheduled => Snapshot(_scheduled);

    public IReadOnlyList<string> Duplicates => Snapshot(_duplicates);

    public StreamCore Core => Stream.Core;

    /// <summary>Builds the client and starts consuming.</summary>
    public StreamProbe Start(
        bool raw,
        JsonObject args,
        string apiKey,
        string nonce,
        Func<FinlightConfig, FinlightConfig>? configure = null,
        bool takeover = false)
    {
        var config = new FinlightConfig
        {
            ApiKey = apiKey,
            WssUrl = _ws.Url,
            BaseUrl = "http://127.0.0.1:9",
            TimeProvider = Clock,
            NonceFactory = () => nonce,
        };
        Config = configure?.Invoke(config) ?? config;
        Client = new FinlightClient(Config);
        var options = new StreamOptions
        {
            Takeover = takeover,
            OnAdmitted = a => Add(_admitted, a),
            OnReconnect = r => Add(_reconnects, r),
            OnError = e => Add(_errors, e),
            OnQuotaWait = q => Add(_quotaWaits, q),
        };
        Stream = raw
            ? Client.RawStreamAsync(args.Deserialize<RawStreamParams>()!, options)
            : Client.StreamAsync(args.Deserialize<StreamParams>()!, options);
        var hooks = Stream.Core.Hooks;
        hooks.OnFrame = _ => Interlocked.Increment(ref _frames);
        hooks.OnPong = () => Interlocked.Increment(ref _pongs);
        hooks.OnPingSent = () => Interlocked.Increment(ref _pingsSent);
        hooks.OnDuplicate = id => Add(_duplicates, id);
        hooks.OnReconnectScheduled = (delay, reason) => Add(_scheduled, (delay, reason, Clock.GetUtcNow()));
        _pump = Task.Run(async () =>
        {
            try
            {
                await foreach (var item in Stream)
                {
                    Add(_items, item);
                }
            }
            catch (Exception exception)
            {
                Terminal = exception;
            }
            finally
            {
                Done = true;
            }
        });
        return this;
    }

    /// <summary>Moves the fake clock in steps, letting the stream handle every timer and pong in between.</summary>
    public async Task AdvanceAsync(TimeSpan total)
    {
        var step = TimeSpan.FromSeconds(20);
        while (total > TimeSpan.Zero)
        {
            var delta = total < step ? total : step;
            Clock.Advance(delta);
            total -= delta;
            await SettleAsync();
        }

        await SettleAsync();
    }

    /// <summary>Waits until the stream's control loop is idle and every server pong was handled.</summary>
    public async Task SettleAsync()
    {
        try
        {
            await Wait.ForAsync(
                () => Done || (Core.Idle && (!_ws.AutoPong || Pongs >= PingsSent)),
                "the stream to settle",
                2000);
        }
        catch (TimeoutException)
        {
            // A pong on a connection that is going away may never be handled; that is fine here.
        }

        await Task.Delay(1);
    }

    public async Task StopAsync()
    {
        await Stream.DisposeAsync();
        if (_pump is not null)
        {
            await _pump.WaitAsync(TimeSpan.FromSeconds(10));
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        Client.Dispose();
    }

    private void Add<T>(List<T> list, T item)
    {
        lock (_lock)
        {
            list.Add(item);
        }
    }

    private List<T> Snapshot<T>(List<T> list)
    {
        lock (_lock)
        {
            return [.. list];
        }
    }
}
