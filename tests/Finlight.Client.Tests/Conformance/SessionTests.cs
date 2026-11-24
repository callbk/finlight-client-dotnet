using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Conformance;

/// <summary>S-01 to S-28 (WebSocket sessions) and R-13, R-14 (subscribe frames) against an in-process server.</summary>
public sealed class SessionTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["S-01"] = "v2-pong-before-subscribe.json",
        ["S-03"] = "admitted-nonce.json",
        ["S-04"] = "created-then-updated.json",
        ["S-05"] = "unknown-event.json",
        ["S-06"] = "ping-on-default-route.json",
        ["S-07"] = "validation-failed-forbidden-key.json",
        ["S-08"] = "raw-include-rejected.json",
        ["S-09"] = "preempted-connection-limit.json",
        ["S-10"] = "preempted-quota-exceeded.json",
        ["S-11"] = "v2-preempt-before-subscribe.json",
        ["S-12"] = "v2-invalid-json-error.json",
        ["S-13"] = "handshake-failed.json",
        ["S-14"] = "search-failed.json",
        ["S-15"] = "internal-error.json",
        ["S-16"] = "pong-watchdog.json",
        ["S-17"] = "raw-stream.json",
        ["S-18"] = "rotation.json",
        ["S-19"] = "dial-missing-key.json",
        ["S-20"] = "dial-invalid-key.json",
        ["S-21"] = "dial-malformed-key.json",
        ["S-22"] = "dial-no-websockets.json",
        ["S-23"] = "dial-too-many-connections.json",
        ["S-24"] = "dial-quota-exceeded.json",
        ["S-25"] = "dial-admit-race.json",
        ["S-26"] = "dial-server-error.json",
        ["S-27"] = "preempted-quota-exceeded-no-reconnect.json",
        ["S-28"] = "dial-quota-exceeded-no-reconnect.json",
    };

    public static TheoryData<string> Cases => [.. Files.Keys.Append("S-02").Append("R-13").Append("R-14").Order()];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string id)
    {
        switch (id)
        {
            case "S-02":
                await RawBytesAsync();
                return;
            case "R-13":
                await SubscribeFrameAsync("ws-subscribe-enriched.json", raw: false);
                return;
            case "R-14":
                await SubscribeFrameAsync("ws-subscribe-raw.json", raw: true);
                return;
        }

        var fixture = Kit.Obj("ws-sessions/" + Files[id]);
        Assert.Equal(id, fixture["id"]!.GetValue<string>());
        await using var ws = await WsMock.StartAsync();
        var runner = id switch
        {
            "S-07" => new SessionRunner(ws, fixture, "includeContent"),
            "S-08" => new SessionRunner(ws, fixture, "include"),
            _ => new SessionRunner(ws, fixture),
        };

        if (id == "S-08")
        {
            // Raw knows no include: the parameter type has none, so it cannot be sent.
            Assert.Null(typeof(RawStreamParams).GetProperty("Include"));
        }

        await runner.RunAsync(After(id));
    }

    private static Func<SessionRunner, Task>? After(string id) => id switch
    {
        "S-05" => r =>
        {
            var other = Assert.Single(r.Probe.Items, i => i.Kind == StreamEventKind.Other);
            Assert.Equal("connection.draining", other.Event.Value);
            Assert.Null(other.Data);
            Assert.Equal(30000, JsonNode.Parse(other.GetRawJson())!["data"]!["inMs"]!.GetValue<int>());
            return Task.CompletedTask;
        }
        ,
        "S-12" => r =>
        {
            var error = Assert.IsAssignableFrom<FinlightApiException>(Assert.Single(r.Probe.Errors));
            Assert.Equal("Message processing failed", error.Detail);
            return Task.CompletedTask;
        }
        ,
        "S-17" => r =>
        {
            var article = r.Probe.Items[0].Data!;
            Assert.Equal(article.FirstSeenAt, r.Probe.Stream.LastSeenAt);
            return Task.CompletedTask;
        }
        ,
        "S-18" => RotationAfterAsync,
        _ => null,
    };

    /// <summary>S-18: the new connection is admitted, the old one closes with 1000, the replayed push is dropped.</summary>
    private static async Task RotationAfterAsync(SessionRunner runner)
    {
        var probe = runner.Probe;
        var pushed = Kit.Obj("ws-sessions/admitted-nonce.json")["steps"]![3]!["frame"]!;
        var admitted = Kit.Obj("ws-sessions/rotation.json")["steps"]![2]!["frame"]!;
        var oldConn = runner.Ws.Dials.First(d => d.Conn is not null).Conn;
        var newConn = runner.Conn!;
        Assert.NotSame(oldConn, newConn);

        // The initial push arrived on the old connection before.
        await oldConn!.SendAsync(pushed);
        await Wait.ForAsync(() => probe.Items.Count == 1, "the first push");
        await newConn.SendAsync(admitted);
        await newConn.SendAsync(pushed);
        await Wait.ForAsync(() => probe.Duplicates.Count == 1, "the replayed push dropped");
        await oldConn.Closed.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1000, oldConn.CloseStatus);
        Assert.True(newConn.Open);
        Assert.Single(probe.Items, i => i.Kind == StreamEventKind.Created);
        var reconnect = Assert.Single(probe.Reconnects);
        Assert.Equal(ReconnectReason.Rotation, reconnect.Reason);
        Assert.Equal(2, reconnect.Connection);
        Assert.Equal(pushed["data"]!["availableAt"]!.GetValue<string>(), probe.Stream.LastSeenAt);
    }

    /// <summary>S-02: a plain TCP listener reads the raw bytes of the dial; the key header is exactly lowercase.</summary>
    private static async Task RawBytesAsync()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var capture = Task.Run(async () =>
            {
                using var tcp = await listener.AcceptTcpClientAsync();
                var stream = tcp.GetStream();
                var buffer = new byte[16 * 1024];
                var total = 0;
                while (!Encoding.ASCII.GetString(buffer, 0, total).Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var read = await stream.ReadAsync(buffer.AsMemory(total));
                    if (read == 0)
                    {
                        break;
                    }

                    total += read;
                }

                return Encoding.ASCII.GetString(buffer, 0, total);
            });

            using var client = new FinlightClient(new FinlightConfig { ApiKey = Kit.ApiKey, WssUrl = $"ws://127.0.0.1:{port}/" });
            await using var stream = client.StreamAsync(new StreamParams { Query = "ticker:XMPL" }, new StreamOptions { Takeover = true });
            var enumerator = stream.GetAsyncEnumerator();
            var pending = enumerator.MoveNextAsync();
            var head = await capture.WaitAsync(TimeSpan.FromSeconds(10));
            var lines = head.Split("\r\n");
            Assert.Equal("GET / HTTP/1.1", lines[0]);
            Assert.DoesNotContain("apikey", lines[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"x-api-key: {Kit.ApiKey}", lines);
            Assert.Contains($"x-client-version: {FinlightClient.Version}", lines);
            Assert.Contains("x-takeover: true", lines);
            Assert.Contains($"User-Agent: finlight-client-dotnet/{FinlightClient.Version}", lines);
            Assert.DoesNotContain(lines, l => l.StartsWith("X-Api-Key:", StringComparison.Ordinal) || l.StartsWith("X-API-KEY:", StringComparison.Ordinal));
            await stream.CloseAsync();
            Assert.False(await pending.AsTask().WaitAsync(TimeSpan.FromSeconds(10)));
            await enumerator.DisposeAsync();
            Assert.True(stream.Closed);
        }
        finally
        {
            listener.Stop();
        }
    }

    /// <summary>R-13, R-14: the exact subscribe frame and the server's answer.</summary>
    private static async Task SubscribeFrameAsync(string file, bool raw)
    {
        var fixture = Kit.Obj("fixtures/requests/" + file);
        var expected = fixture["frame"]!.AsObject();
        await using var ws = await WsMock.StartAsync();
        var probe = new StreamProbe(ws).Start(raw, fixture["sdk"]!["args"]!.AsObject(), Kit.ApiKey, expected["clientNonce"]!.GetValue<string>());
        try
        {
            var dial = await ws.NextDialAsync();
            Assert.Equal(raw ? "/raw" : "/", dial.Path);
            var sent = (await dial.Conn!.NextAsync("the subscribe"))!.AsObject();
            Assert.Equal(Kit.Canonical(expected), Kit.Canonical(sent));
            Assert.Equal(System.Text.Json.JsonValueKind.Number, sent["payloadVersion"]!.GetValueKind());
            Assert.False(sent.ContainsKey("action"));
            foreach (var key in new[] { "includeContent", "includeEntities", "extended" })
            {
                Assert.False(sent.ContainsKey(key));
            }

            if (raw)
            {
                Assert.False(sent.ContainsKey("include"));
            }

            var frames = fixture["server"]!["frames"]!.AsArray();
            foreach (var frame in frames)
            {
                await dial.Conn.SendAsync(frame!);
            }

            await Wait.ForAsync(() => probe.Frames >= frames.Count, "the server frames");
            var admitted = Assert.Single(probe.Admitted);
            Assert.Equal(expected["clientNonce"]!.GetValue<string>(), admitted.ClientNonce);
            Assert.Empty(probe.Errors);
            if (raw)
            {
                Assert.Empty(probe.Items);
            }
            else
            {
                var item = Assert.Single(probe.Items);
                Assert.Equal(StreamEventKind.Created, item.Kind);
                Assert.Equal(frames[1]!["data"]!["id"]!.GetValue<string>(), item.Data!.Id);
            }
        }
        finally
        {
            await probe.DisposeAsync();
        }
    }
}
