using System.Threading.Channels;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests.Support;

/// <summary>A <see cref="FakeTimeProvider"/> that reports each timer the client starts, so a test can wait for a wait to begin.</summary>
internal sealed class RecordingClock(DateTimeOffset start) : FakeTimeProvider(start)
{
    private readonly Channel<TimeSpan> _timers = Channel.CreateUnbounded<TimeSpan>();

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = base.CreateTimer(callback, state, dueTime, period);
        _timers.Writer.TryWrite(dueTime);
        return timer;
    }

    /// <summary>The due time of the next timer started on this clock; fails after <paramref name="timeout"/> of real time.</summary>
    public async Task<TimeSpan> NextTimerAsync(TimeSpan? timeout = null)
    {
        using var cts = new CancellationTokenSource(timeout ?? TimeSpan.FromSeconds(10));
        return await _timers.Reader.ReadAsync(cts.Token);
    }
}
