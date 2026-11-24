using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Unit;

/// <summary>Stream rules beyond the kit sessions (R-REQ-1, R-WS-2, R-WS-5, R-WS-6, R-LOG-1).</summary>
public sealed class StreamRulesTests
{
    private const string Admitted = """{"event":"connection.admitted","payloadVersion":3,"data":{"leaseId":"l","serverNow":1,"clientNonce":"n"}}""";

    private static string Article(string id, string evt = "article.created", string? availableAt = "2026-11-24T00:00:00.000Z", string firstSeenAt = "2026-11-23T00:00:00.000Z")
        => new JsonObject
        {
            ["event"] = evt,
            ["payloadVersion"] = 3,
            ["data"] = new JsonObject
            {
                ["id"] = id,
                ["link"] = "https://x.example/" + id,
                ["source"] = new JsonObject { ["domain"] = "x.example" },
                ["title"] = "t",
                ["availableAt"] = availableAt,
                ["firstSeenAt"] = firstSeenAt,
            },
        }.ToJsonString();

    [Fact]
    public async Task Subscribe_HoldsOnlyTheSetKeysPlusPayloadVersionAndNonce()
    {
        await using var ws = await WsMock.StartAsync();
        await using var probe = new StreamProbe(ws).Start(false, new JsonObject { ["query"] = "ticker:XMPL" }, Kit.ApiKey, "n-1");
        var dial = await ws.NextDialAsync();
        var sent = (await dial.Conn!.NextAsync("subscribe"))!.AsObject();
        Assert.Equal("""{"clientNonce":"n-1","payloadVersion":3,"query":"ticker:XMPL"}""", Kit.Canonical(sent));
    }

    [Fact]
    public async Task Hooks_ThatThrowDoNotEndTheStream()
    {
        await using var ws = await WsMock.StartAsync();
        using var client = new FinlightClient(new FinlightConfig { ApiKey = Kit.ApiKey, WssUrl = ws.Url });
        await using var stream = client.StreamAsync(new StreamParams(), new StreamOptions
        {
            OnAdmitted = _ => throw new InvalidOperationException("user bug"),
            OnError = _ => throw new InvalidOperationException("user bug"),
        });
        var items = new List<StreamEvent>();
        var consumer = Task.Run(async () =>
        {
            await foreach (var item in stream)
            {
                items.Add(item);
                if (items.Count == 2)
                {
                    break;
                }
            }
        });
        var conn = (await ws.NextDialAsync()).Conn!;
        await conn.NextAsync("subscribe");
        await conn.SendAsync(Admitted);
        await conn.SendAsync("""{"event":"connection.error","payloadVersion":3,"data":{"code":"INTERNAL_ERROR","message":"m"}}""");
        await conn.SendAsync(Article("a"));
        await conn.SendAsync(Article("a"));
        await conn.SendAsync(Article("a", "article.updated"));
        await consumer.WaitAsync(TimeSpan.FromSeconds(10));

        // created and updated of the same id are two items; the exact repeat is dropped.
        Assert.Equal([StreamEventKind.Created, StreamEventKind.Updated], items.Select(i => i.Kind));

        // Leaving the loop closed the socket with 1000.
        await conn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000, conn.CloseStatus);
        Assert.True(stream.Closed);
        Assert.Equal(0, stream.Core.OpenTimers);
    }

    [Fact]
    public async Task Cancellation_EndsTheIterationAsACancel()
    {
        await using var ws = await WsMock.StartAsync();
        using var client = new FinlightClient(new FinlightConfig { ApiKey = Kit.ApiKey, WssUrl = ws.Url });
        using var cts = new CancellationTokenSource();
        var stream = client.StreamAsync(new StreamParams(), null, cts.Token);
        var consumer = Task.Run(async () =>
        {
            await foreach (var _ in stream)
            {
            }
        });
        var conn = (await ws.NextDialAsync()).Conn!;
        await conn.NextAsync("subscribe");
        await cts.CancelAsync();
        var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(cts.Token, canceled.CancellationToken);
        await conn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000, conn.CloseStatus);
        Assert.Equal(0, stream.Core.OpenTimers);
        Assert.Equal(0, stream.Core.OpenConnections);
        Assert.Throws<FinlightValidationException>(() => stream.GetAsyncEnumerator());
    }

    [Fact]
    public async Task Refusal_WithoutBody_401IsTerminal_403WaitsLongAndReports()
    {
        await using (var ws = await WsMock.StartAsync())
        {
            ws.DialScript.Enqueue((401, null));
            await using var probe = new StreamProbe(ws).Start(false, [], Kit.ApiKey, "n");
            await Wait.ForAsync(() => probe.Done, "the terminal error");
            Assert.IsType<UnauthorizedException>(probe.Terminal);
        }

        await using (var ws = await WsMock.StartAsync())
        {
            ws.DialScript.Enqueue((403, null));
            await using var probe = new StreamProbe(ws).Start(false, [], Kit.ApiKey, "n");
            await Wait.ForAsync(() => probe.Scheduled.Count == 1, "the reconnect");
            Assert.True(probe.Scheduled[0].Delay >= TimeSpan.FromSeconds(60));
            Assert.Equal(ReconnectReason.DialRefused, probe.Scheduled[0].Reason);
            var error = Assert.IsType<ForbiddenException>(Assert.Single(probe.Errors));
            Assert.Equal(403, error.Status);
            Assert.NotNull(error.InnerException);
            Assert.False(probe.Done);
        }
    }

    [Fact]
    public async Task Reconnect_ReportsLastSeenAtWithoutBackfill()
    {
        await using var ws = await WsMock.StartAsync();
        await using var probe = new StreamProbe(ws).Start(true, [], Kit.ApiKey, "n");
        var conn = (await ws.NextDialAsync()).Conn!;
        await conn.NextAsync("subscribe");
        await conn.SendAsync(Admitted);
        await conn.SendAsync(Article("r1", availableAt: null, firstSeenAt: "2026-11-23T10:00:00.000Z"));
        await Wait.ForAsync(() => probe.Items.Count == 1, "the item");
        await conn.CloseAsync();
        await Wait.ForAsync(() => probe.Scheduled.Count == 1, "the reconnect");
        await probe.AdvanceAsync(probe.Scheduled[0].Delay);
        var second = (await ws.NextDialAsync()).Conn!;
        await second.NextAsync("re-subscribe");
        await Wait.ForAsync(() => probe.Reconnects.Count == 1, "onReconnect");
        Assert.Equal("2026-11-23T10:00:00.000Z", probe.Reconnects[0].LastSeenAt);
        Assert.Equal(ReconnectReason.Closed, probe.Reconnects[0].Reason);
        Assert.Equal(2, probe.Reconnects[0].Connection);
        Assert.Equal(2, ws.DialCount);
    }

    [Fact]
    public async Task Logging_NeverShowsTheKey()
    {
        const string key = "sk_secret_stream_key_0123456789";
        var logs = new CapturingLoggerFactory();
        await using var ws = await WsMock.StartAsync();
        ws.DialScript.Enqueue((403, """{"message":"Too many concurrent connections"}"""));
        await using var probe = new StreamProbe(ws).Start(false, [], key, "n", c => c with { LoggerFactory = logs });
        await Wait.ForAsync(() => probe.Scheduled.Count == 1, "the reconnect");
        Assert.NotEmpty(logs.Lines);
        foreach (var text in logs.Lines.Select(l => l.Text).Concat(probe.Errors.Select(e => e.ToString())))
        {
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        }
    }

    private const string TooMany = """{"message":"Too many concurrent connections. Use x-takeover: true header to replace an existing connection."}""";

    private const string PreemptConnectionLimit = """{"event":"connection.preempted","payloadVersion":3,"data":{"reason":"connection_limit","newLeaseId":"l2","timestamp":1}}""";

    [Fact]
    public async Task Rotation_WithTakeoverAtTheLimitSurvivesThePreemptOfTheOldConnection()
    {
        await using var ws = await WsMock.StartAsync();
        await using var probe = new StreamProbe(ws).Start(
            false, [], Kit.ApiKey, "n", c => c with { RotationInterval = TimeSpan.FromMinutes(2) }, takeover: true);
        var first = (await ws.NextDialAsync()).Conn!;
        await first.NextAsync("subscribe");
        await first.SendAsync(Admitted);
        await Wait.ForAsync(() => probe.Admitted.Count == 1, "admitted");

        await probe.AdvanceAsync(TimeSpan.FromMinutes(2));
        var dial = await ws.NextDialAsync("the rotation dial");
        Assert.Equal("true", dial.Headers["x-takeover"]);
        var second = dial.Conn!;
        await second.NextAsync("re-subscribe", skipPings: true);

        // Limit 1 with takeover: the server preempts the stalest connection, which is our own old one.
        await first.SendAsync(PreemptConnectionLimit);
        await first.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        await second.SendAsync(Admitted);
        await Wait.ForAsync(() => probe.Admitted.Count == 2, "admitted on the new connection");
        await Wait.SettleAsync();
        Assert.False(probe.Done);
        Assert.Null(probe.Terminal);
        Assert.Empty(probe.Errors);
        Assert.True(second.Open);
        Assert.Equal(1, probe.Core.OpenConnections);

        // A preempt of the current connection is still terminal.
        await second.SendAsync(PreemptConnectionLimit);
        await Wait.ForAsync(() => probe.Done, "the terminal preempt");
        var preempted = Assert.IsType<PreemptedException>(probe.Terminal);
        Assert.Equal(PreemptReason.ConnectionLimit, preempted.Reason);
    }

    [Fact]
    public async Task Rotation_WithoutRoomForASecondConnectionClosesTheOldOneFirst()
    {
        await using var ws = await WsMock.StartAsync();
        ws.DialScript.Enqueue((101, null));
        ws.DialScript.Enqueue((403, TooMany)); // make-before-break: the old connection holds the only slot
        ws.DialScript.Enqueue((403, TooMany)); // the server noticed the close a little late
        await using var probe = new StreamProbe(ws).Start(
            false, [], Kit.ApiKey, "n", c => c with { RotationInterval = TimeSpan.FromMinutes(2) });
        var first = (await ws.NextDialAsync()).Conn!;
        await first.NextAsync("subscribe");
        await first.SendAsync(Admitted);
        await Wait.ForAsync(() => probe.Admitted.Count == 1, "admitted");

        await probe.AdvanceAsync(TimeSpan.FromMinutes(2));
        Assert.Null((await ws.NextDialAsync("the refused rotation dial")).Conn);
        await first.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000, first.CloseStatus);

        // Two quiet short retries, no error report, no long backoff.
        await Wait.ForAsync(() => probe.Scheduled.Count == 2, "the reconnect after the close");
        Assert.Equal(ReconnectReason.Rotation, probe.Scheduled[1].Reason);
        Assert.True(probe.Scheduled[1].Delay < TimeSpan.FromSeconds(5));
        await probe.AdvanceAsync(probe.Scheduled[1].Delay);
        Assert.Null((await ws.NextDialAsync("the late-refused redial")).Conn);
        await Wait.ForAsync(() => probe.Scheduled.Count == 3, "a quiet retry");
        Assert.Equal(ReconnectReason.Rotation, probe.Scheduled[2].Reason);
        Assert.True(probe.Scheduled[2].Delay < TimeSpan.FromSeconds(5));
        await probe.AdvanceAsync(probe.Scheduled[2].Delay);
        var second = (await ws.NextDialAsync("the redial")).Conn!;
        await second.NextAsync("re-subscribe", skipPings: true);
        await second.SendAsync(Admitted);
        await Wait.ForAsync(() => probe.Reconnects.Count == 1, "onReconnect");
        Assert.Equal(ReconnectReason.Rotation, probe.Reconnects[0].Reason);
        Assert.Empty(probe.Errors);
        Assert.False(probe.Done);

        // The next rotation closes first and never dials a second connection next to the live one.
        var dials = ws.DialCount;
        await probe.AdvanceAsync(TimeSpan.FromMinutes(2) - TimeSpan.FromMilliseconds(1));
        Assert.Equal(dials, ws.DialCount);
        await probe.AdvanceAsync(TimeSpan.FromMilliseconds(1));
        await second.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(dials, ws.DialCount);
        await Wait.ForAsync(() => probe.Scheduled.Count == 4, "the reconnect of the second rotation");
        await probe.AdvanceAsync(probe.Scheduled[3].Delay);
        var third = (await ws.NextDialAsync("the second rotation's dial")).Conn!;
        await third.NextAsync("re-subscribe", skipPings: true);
        Assert.Empty(probe.Errors);
    }

    [Fact]
    public async Task Watchdog_AV2PongRefreshesIt()
    {
        await using var ws = await WsMock.StartAsync();
        ws.AutoPong = false;
        await using var probe = new StreamProbe(ws).Start(false, [], Kit.ApiKey, "n");
        var conn = (await ws.NextDialAsync()).Conn!;
        await conn.NextAsync("subscribe");
        await probe.AdvanceAsync(TimeSpan.FromSeconds(50));
        await conn.SendAsync("""{"action":"pong","t":1,"serverNow":1}""");
        await Wait.ForAsync(() => probe.Pongs == 1, "the v2 pong");

        // 100 s after the connect but only 59 s after the pong: no reconnect yet.
        await probe.AdvanceAsync(TimeSpan.FromSeconds(59));
        Assert.Empty(probe.Scheduled);
        Assert.True(conn.Open);

        await probe.AdvanceAsync(TimeSpan.FromSeconds(2));
        await Wait.ForAsync(() => probe.Scheduled.Count == 1, "the watchdog reconnect");
        Assert.Equal(ReconnectReason.Watchdog, probe.Scheduled[0].Reason);
    }

    [Fact]
    public async Task DialFailure_KeepsTheOriginalErrorAsTheCause()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using var ws = await WsMock.StartAsync();
        await using var probe = new StreamProbe(ws).Start(false, [], Kit.ApiKey, "n", c => c with { WssUrl = $"ws://127.0.0.1:{port}" });
        await Wait.ForAsync(() => probe.Errors.Count == 1, "the reported dial failure");
        var error = Assert.IsType<FinlightNetworkException>(probe.Errors[0]);
        Assert.NotNull(error.InnerException);
        Assert.False(probe.Done);
    }

    [Fact]
    public async Task Dispose_OfTheClientClosesItsStreams()
    {
        await using var ws = await WsMock.StartAsync();
        await using var probe = new StreamProbe(ws).Start(false, [], Kit.ApiKey, "n");
        var conn = (await ws.NextDialAsync()).Conn!;
        await conn.NextAsync("subscribe");
        await conn.SendAsync(Admitted);
        await Wait.ForAsync(() => probe.Admitted.Count == 1, "admitted");

        probe.Client.Dispose();
        await Wait.ForAsync(() => probe.Done, "the stream to end");
        Assert.Null(probe.Terminal);
        await conn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000, conn.CloseStatus);
        Assert.Equal(0, probe.Core.OpenTimers);
        Assert.Equal(0, probe.Core.OpenConnections);

        // Nothing redials afterwards, and a disposed client opens no new stream.
        probe.Clock.Advance(TimeSpan.FromHours(3));
        await Wait.SettleAsync();
        Assert.Equal(1, ws.DialCount);
        Assert.Throws<ObjectDisposedException>(() => probe.Client.StreamAsync(new StreamParams()));
    }

    [Fact]
    public void Defaults_PingWatchdogRotation()
    {
        var config = new FinlightConfig { ApiKey = "k" };
        Assert.Equal(TimeSpan.FromSeconds(25), config.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), config.WatchdogTimeout);
        Assert.Equal(TimeSpan.FromMinutes(115), config.RotationInterval);
        Assert.False(new StreamOptions().Takeover);
    }
}
