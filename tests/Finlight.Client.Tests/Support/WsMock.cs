using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Finlight.Tests.Support;

/// <summary>A dial the server saw.</summary>
internal sealed record DialRecord(string Path, string RawUrl, IReadOnlyDictionary<string, string> Headers, ServerConn? Conn);

/// <summary>The server side of one accepted WebSocket connection.</summary>
internal sealed class ServerConn
{
    private readonly WebSocket _ws;
    private readonly Func<bool> _autoPong;
    private readonly Action _onPong;
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly List<JsonNode?> _messages = [];
    private readonly TaskCompletionSource _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _consumed;

    public ServerConn(WebSocket ws, Func<bool> autoPong, Action onPong)
    {
        _ws = ws;
        _autoPong = autoPong;
        _onPong = onPong;
    }

    public int? CloseStatus { get; private set; }

    public bool Open => _ws.State == WebSocketState.Open;

    public Task Closed => _closed.Task;

    public IReadOnlyList<JsonNode?> Messages
    {
        get
        {
            lock (_messages)
            {
                return [.. _messages];
            }
        }
    }

    public async Task RunAsync()
    {
        var buffer = new byte[1 << 16];
        try
        {
            while (true)
            {
                using var message = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await _ws.ReceiveAsync(buffer, CancellationToken.None);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        CloseStatus = (int?)result.CloseStatus;
                        if (_ws.State == WebSocketState.CloseReceived)
                        {
                            await _send.WaitAsync();
                            try
                            {
                                await _ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                            }
                            finally
                            {
                                _send.Release();
                            }
                        }

                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage);

                JsonNode? node;
                try
                {
                    node = JsonNode.Parse(message.ToArray());
                }
                catch (System.Text.Json.JsonException)
                {
                    node = JsonValue.Create(Encoding.UTF8.GetString(message.ToArray()));
                }

                lock (_messages)
                {
                    _messages.Add(node);
                }

                if (_autoPong() && node is JsonObject obj && obj["action"]?.GetValue<string>() == "ping")
                {
                    await SendAsync(new JsonObject
                    {
                        ["event"] = "connection.pong",
                        ["payloadVersion"] = 3,
                        ["data"] = new JsonObject { ["t"] = obj["t"]?.DeepClone(), ["serverNow"] = 1 },
                    }.ToJsonString());
                    _onPong();
                }
            }
        }
        catch (Exception)
        {
            // aborted by the client
        }
        finally
        {
            CloseStatus ??= (int?)_ws.CloseStatus;
            _closed.TrySetResult();
        }
    }

    public async Task<JsonNode?> NextAsync(string what, bool skipPings = false)
    {
        while (true)
        {
            await Wait.ForAsync(() => Messages.Count > _consumed, what);
            var message = Messages[_consumed++];
            if (skipPings && message is JsonObject obj && obj["action"]?.ToString() == "ping")
            {
                continue;
            }

            return message;
        }
    }

    public JsonNode? Peek() => Messages.Count > _consumed ? Messages[_consumed] : null;

    public void Skip() => _consumed++;

    public async Task SendAsync(string text)
    {
        await _send.WaitAsync();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                await _ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        }
        finally
        {
            _send.Release();
        }
    }

    public Task SendAsync(JsonNode frame) => SendAsync(frame.ToJsonString());

    public async Task CloseAsync(WebSocketCloseStatus status = WebSocketCloseStatus.NormalClosure)
    {
        await _send.WaitAsync();
        try
        {
            if (_ws.State == WebSocketState.Open)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await _ws.CloseOutputAsync(status, null, cts.Token);
            }
        }
        catch (Exception)
        {
            _ws.Abort();
        }
        finally
        {
            _send.Release();
        }
    }
}

/// <summary>In-process WebSocket server on 127.0.0.1 with scripted dial answers.</summary>
internal sealed class WsMock : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly List<DialRecord> _dials = [];
    private int _consumedDials;
    private int _pongsSent;

    private WsMock(WebApplication app) => _app = app;

    public string Url { get; private set; } = string.Empty;

    public ConcurrentQueue<(int Status, string? Body)> DialScript { get; } = new();

    public bool AutoPong { get; set; } = true;

    public int PongsSent => Volatile.Read(ref _pongsSent);

    public IReadOnlyList<DialRecord> Dials
    {
        get
        {
            lock (_dials)
            {
                return [.. _dials];
            }
        }
    }

    public int DialCount => Dials.Count;

    public static async Task<WsMock> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();
        var mock = new WsMock(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        mock.Url = app.Urls.First().Replace("http://", "ws://", StringComparison.Ordinal);
        return mock;
    }

    public async Task<DialRecord> NextDialAsync(string what = "a dial")
    {
        await Wait.ForAsync(() => DialCount > _consumedDials, what);
        return Dials[_consumedDials++];
    }

    public void ConsumeAllDials() => _consumedDials = DialCount;

    public int OpenConnections() => Dials.Count(d => d.Conn is { Open: true });

    private async Task HandleAsync(HttpContext context)
    {
        var headers = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (name, values) in context.Request.Headers)
        {
            headers[name] = values.ToString();
        }

        var path = context.Request.Path.Value ?? "/";
        var rawUrl = path + context.Request.QueryString.Value;
        var answer = DialScript.TryDequeue(out var scripted) ? scripted : (101, null);
        if (answer.Status != 101 || !context.WebSockets.IsWebSocketRequest)
        {
            lock (_dials)
            {
                _dials.Add(new DialRecord(path, rawUrl, headers, null));
            }

            context.Response.StatusCode = answer.Status == 101 ? 400 : answer.Status;
            context.Response.ContentType = "application/json";
            if (answer.Body is not null)
            {
                await context.Response.WriteAsync(answer.Body);
            }

            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        var conn = new ServerConn(socket, () => AutoPong, () => Interlocked.Increment(ref _pongsSent));
        lock (_dials)
        {
            _dials.Add(new DialRecord(path, rawUrl, headers, conn));
        }

        await conn.RunAsync();
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
    }
}

/// <summary>Polls a condition in real time (the SDK runs on its own tasks).</summary>
internal static class Wait
{
    public static async Task ForAsync(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        var started = Environment.TickCount64;
        if (condition())
        {
            return;
        }

        while (!condition())
        {
            if (Environment.TickCount64 - started > timeoutMs)
            {
                throw new TimeoutException($"timed out waiting for {what}");
            }

            await Task.Delay(2);
        }
    }

    /// <summary>Gives the SDK's tasks time to act on something that must not happen.</summary>
    public static Task SettleAsync(int ms = 40) => Task.Delay(ms);
}
