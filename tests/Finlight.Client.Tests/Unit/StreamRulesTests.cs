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
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => consumer.WaitAsync(TimeSpan.FromSeconds(10)));
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
