using System.Net.WebSockets;
using System.Text.Json;
using Finlight.Tests.Support;
using Finlight.WebSockets;
using Microsoft.Extensions.Time.Testing;

namespace Finlight.Tests;

public class WebSocketProtocolTests
{
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    /// <summary>Keeps reconnect tests quick; mirrors the Go client's fastWSOptions.</summary>
    private static FinlightWebSocketOptions FastOptions(Action<int, string>? onClose = null) => new()
    {
        BaseReconnectDelay = TimeSpan.FromMilliseconds(10),
        MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
        OnClose = onClose,
    };

    private static FinlightClientOptions ClientOptions(string url, TimeProvider? time = null) => new()
    {
        ApiKey = "test-key",
        WssUrl = url,
        Timeout = TimeSpan.FromSeconds(5),
        TimeProvider = time ?? TimeProvider.System,
    };

    private static async Task<List<T>> DrainAsync<T>(IAsyncEnumerable<T> stream)
    {
        var items = new List<T>();
        await foreach (var item in stream)
        {
            items.Add(item);
        }

        return items;
    }

    [Fact]
    public async Task Stream_HandshakeDedupAndPreempt()
    {
        JsonDocument? handshake = null;
        string? apiKey = null, clientVersion = null, takeover = null;
        await using var server = await WsTestServer.StartAsync(async (context, socket) =>
        {
            apiKey = context.Request.Headers["x-api-key"];
            clientVersion = context.Request.Headers["x-client-version"];
            takeover = context.Request.Headers["x-takeover"];
            handshake = await WsTestServer.ReceiveJsonAsync(socket);
            var nonce = handshake.RootElement.GetProperty("clientNonce").GetString();
            await WsTestServer.SendJsonAsync(socket, new { action = "admit", leaseId = "lease-1", clientNonce = nonce });
            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/a"));
            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/a"));
            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/b"));
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted", reason = "test over" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new ArticleWebSocketClient(
            ClientOptions(server.Url), new FinlightWebSocketOptions { Takeover = true });
        var articles = await DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams { Query = "nvidia" }))
            .WaitAsync(TestTimeout);

        Assert.Equal(
            ["https://example.com/a", "https://example.com/b"],
            articles.Select(article => article.Link));
        Assert.Equal("test-key", apiKey);
        Assert.StartsWith("dotnet/Finlight.Client@", clientVersion);
        Assert.Equal("true", takeover);
        Assert.NotNull(handshake);
        Assert.Equal("nvidia", handshake.RootElement.GetProperty("query").GetString());
        Assert.Equal(36, handshake.RootElement.GetProperty("clientNonce").GetString()?.Length);
    }

    [Fact]
    public async Task RawStream_DoesNotDedupAndUsesRawPath()
    {
        string? path = null;
        await using var server = await WsTestServer.StartAsync(async (context, socket) =>
        {
            path = context.Request.Path;
            await WsTestServer.ReceiveJsonAsync(socket);
            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/a"));
            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/a"));
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new RawArticleWebSocketClient(ClientOptions(server.Url));
        var articles = await DrainAsync(client.StreamAsync(new GetRawArticlesWebSocketParams()))
            .WaitAsync(TestTimeout);

        Assert.Equal(2, articles.Count);
        Assert.Equal("/raw", path);
    }

    [Fact]
    public async Task Stream_BlockedCloseCode_ThrowsAndReportsOnClose()
    {
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            await WsTestServer.ReceiveJsonAsync(socket);
            await socket.CloseAsync((WebSocketCloseStatus)1008, "blocked", CancellationToken.None);
        });

        var closeCodes = new List<int>();
        var client = new ArticleWebSocketClient(
            ClientOptions(server.Url), FastOptions((code, _) => closeCodes.Add(code)));

        await Assert
            .ThrowsAsync<FinlightBlockedException>(
                () => DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams())).WaitAsync(TestTimeout));
        Assert.Contains(1008, closeCodes);
    }

    [Fact]
    public async Task Stream_ReconnectsAfterServerClose()
    {
        var connections = 0;
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            var n = Interlocked.Increment(ref connections);
            await WsTestServer.ReceiveJsonAsync(socket);
            if (n == 1)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
                return;
            }

            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/after-reconnect"));
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new ArticleWebSocketClient(ClientOptions(server.Url), FastOptions());
        var articles = await DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams()))
            .WaitAsync(TestTimeout);

        Assert.Equal(2, Volatile.Read(ref connections));
        Assert.Equal(["https://example.com/after-reconnect"], articles.Select(article => article.Link));
    }

    [Fact]
    public async Task Stream_ConsumerBreak_ClosesConnection()
    {
        var serverSawClose = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            await WsTestServer.ReceiveJsonAsync(socket);
            for (var i = 0; i < 5; i++)
            {
                await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage($"https://example.com/{i}"));
            }

            await WsTestServer.WaitCloseAsync(socket);
            serverSawClose.TrySetResult();
        });

        var client = new ArticleWebSocketClient(ClientOptions(server.Url));
        var count = 0;
        var consume = async () =>
        {
            await foreach (var _ in client.StreamAsync(new GetArticlesWebSocketParams()))
            {
                count++;
                break;
            }
        };
        await consume().WaitAsync(TestTimeout);

        Assert.Equal(1, count);
        await serverSawClose.Task.WaitAsync(TestTimeout);
    }

    [Fact]
    public async Task Stream_Cancellation_ThrowsOperationCanceled()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            await WsTestServer.ReceiveJsonAsync(socket);
            connected.TrySetResult();
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new ArticleWebSocketClient(ClientOptions(server.Url));
        using var cts = new CancellationTokenSource();
        var streamTask = DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams(), cts.Token));

        await connected.Task.WaitAsync(TestTimeout);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => streamTask.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Stream_SecondConcurrentStream_Throws()
    {
        var connected = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            await WsTestServer.ReceiveJsonAsync(socket);
            connected.TrySetResult();
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new ArticleWebSocketClient(ClientOptions(server.Url));
        using var cts = new CancellationTokenSource();
        var firstStream = DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams(), cts.Token));
        await connected.Task.WaitAsync(TestTimeout);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams())).WaitAsync(TestTimeout));

        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => firstStream.WaitAsync(TestTimeout));
    }

    [Fact]
    public async Task Stream_SendsApplicationPing()
    {
        var ping = new TaskCompletionSource<JsonDocument>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            await WsTestServer.ReceiveJsonAsync(socket);
            var message = await WsTestServer.ReceiveJsonAsync(socket);
            ping.TrySetResult(message);
            await WsTestServer.SendJsonAsync(
                socket, new { action = "pong", t = message.RootElement.GetProperty("t").GetInt64() });
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        var options = new FinlightWebSocketOptions
        {
            PingInterval = TimeSpan.FromMilliseconds(30),
            BaseReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
        };
        var client = new ArticleWebSocketClient(ClientOptions(server.Url), options);
        await DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams())).WaitAsync(TestTimeout);

        var pingMessage = await ping.Task.WaitAsync(TestTimeout);
        Assert.Equal("ping", pingMessage.RootElement.GetProperty("action").GetString());
        Assert.True(pingMessage.RootElement.GetProperty("t").GetInt64() > 0);
    }

    [Fact]
    public async Task Stream_AdminKick_DelaysReconnect()
    {
        const int RetryAfterMs = 300;
        var connections = 0;
        long firstCloseMs = 0, secondDialMs = 0;
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            var n = Interlocked.Increment(ref connections);
            if (n == 1)
            {
                await WsTestServer.ReceiveJsonAsync(socket);
                await WsTestServer.SendJsonAsync(socket, new { action = "admin_kick", retryAfter = RetryAfterMs });
                await WsTestServer.WaitCloseAsync(socket);
                Volatile.Write(ref firstCloseMs, Environment.TickCount64);
                return;
            }

            Volatile.Write(ref secondDialMs, Environment.TickCount64);
            await WsTestServer.ReceiveJsonAsync(socket);
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new ArticleWebSocketClient(ClientOptions(server.Url), FastOptions());
        await DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams())).WaitAsync(TestTimeout);

        Assert.Equal(2, Volatile.Read(ref connections));
        var elapsed = Volatile.Read(ref secondDialMs) - Volatile.Read(ref firstCloseMs);
        Assert.True(
            elapsed >= RetryAfterMs - 100,
            $"reconnected after {elapsed}ms, want >= ~{RetryAfterMs}ms (admin_kick retryAfter)");
    }

    [Fact]
    public async Task Stream_RateLimitError_WaitsBeforeReconnect()
    {
        var connections = 0;
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            var n = Interlocked.Increment(ref connections);
            await WsTestServer.ReceiveJsonAsync(socket);
            if (n == 1)
            {
                await WsTestServer.SendJsonAsync(socket, new { action = "error", data = "Rate limit exceeded" });
                await WsTestServer.WaitCloseAsync(socket);
                return;
            }

            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        // The 60s rate-limit wait runs on fake time; a huge pong timeout keeps
        // the watchdog quiet while the advancer skips ahead.
        var time = new FakeTimeProvider();
        var options = new FinlightWebSocketOptions
        {
            BaseReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
            PongTimeout = TimeSpan.FromHours(2),
            ConnectionLifetime = TimeSpan.FromHours(12),
        };
        var client = new ArticleWebSocketClient(ClientOptions(server.Url, time), options);

        var streamTask = DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams()));
        for (var i = 0; i < 500 && !streamTask.IsCompleted; i++)
        {
            await Task.Delay(10);
            time.Advance(TimeSpan.FromSeconds(10));
        }

        await streamTask.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref connections));
    }

    [Fact]
    public async Task Stream_Handshake429_BacksOffAndRetries()
    {
        var requests = 0;
        var connections = 0;
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            Interlocked.Increment(ref connections);
            await WsTestServer.ReceiveJsonAsync(socket);
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });
        server.OnRequest = context =>
        {
            if (Interlocked.Increment(ref requests) == 1)
            {
                context.Response.StatusCode = 429;
                return false;
            }

            return true;
        };

        var time = new FakeTimeProvider();
        var options = new FinlightWebSocketOptions
        {
            BaseReconnectDelay = TimeSpan.FromMilliseconds(10),
            MaxReconnectDelay = TimeSpan.FromMilliseconds(50),
            PongTimeout = TimeSpan.FromHours(2),
            ConnectionLifetime = TimeSpan.FromHours(12),
        };
        var client = new ArticleWebSocketClient(ClientOptions(server.Url, time), options);

        var streamTask = DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams()));
        for (var i = 0; i < 500 && !streamTask.IsCompleted; i++)
        {
            await Task.Delay(10);
            time.Advance(TimeSpan.FromSeconds(10));
        }

        await streamTask.WaitAsync(TestTimeout);
        Assert.Equal(2, Volatile.Read(ref requests));
        Assert.Equal(1, Volatile.Read(ref connections));
    }

    [Fact]
    public async Task Stream_OnCloseCallbackException_IsSwallowed()
    {
        await using var server = await WsTestServer.StartAsync(async (_, socket) =>
        {
            await WsTestServer.ReceiveJsonAsync(socket);
            await WsTestServer.SendJsonAsync(socket, WsTestServer.ArticleMessage("https://example.com/a"));
            await WsTestServer.SendJsonAsync(socket, new { action = "preempted" });
            await WsTestServer.WaitCloseAsync(socket);
        });

        var client = new ArticleWebSocketClient(
            ClientOptions(server.Url),
            FastOptions((_, _) => throw new InvalidOperationException("callback boom")));
        var articles = await DrainAsync(client.StreamAsync(new GetArticlesWebSocketParams()))
            .WaitAsync(TestTimeout);

        Assert.Single(articles);
    }
}
