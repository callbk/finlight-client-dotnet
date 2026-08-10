using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Finlight.Tests.Support;

/// <summary>
/// In-process Kestrel WebSocket server. The handler runs once per accepted
/// connection; non-WebSocket requests (and requests rejected by
/// <see cref="OnRequest"/>) never reach it.
/// </summary>
internal sealed class WsTestServer : IAsyncDisposable
{
    private static readonly TimeSpan IoTimeout = TimeSpan.FromSeconds(10);

    private readonly WebApplication _app;

    private WsTestServer(WebApplication app, string url)
    {
        _app = app;
        Url = url;
    }

    /// <summary>The server's ws:// base URL.</summary>
    public string Url { get; }

    /// <summary>Optional hook that runs before the upgrade; return false to short-circuit
    /// (set the response status yourself, e.g. 429).</summary>
    public Func<HttpContext, bool>? OnRequest { get; set; }

    public static async Task<WsTestServer> StartAsync(Func<HttpContext, WebSocket, Task> handler)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        app.UseWebSockets();

        WsTestServer? server = null;
        app.Run(async context =>
        {
            if (server?.OnRequest is { } onRequest && !onRequest(context))
            {
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            await handler(context, socket);
        });

        await app.StartAsync();
        var url = app.Urls.First().Replace("http://", "ws://");
        server = new WsTestServer(app, url);
        return server;
    }

    public static async Task SendJsonAsync(WebSocket socket, object value)
    {
        using var cts = new CancellationTokenSource(IoTimeout);
        var data = JsonSerializer.SerializeToUtf8Bytes(value);
        await socket.SendAsync(data, WebSocketMessageType.Text, endOfMessage: true, cts.Token);
    }

    public static async Task<JsonDocument> ReceiveJsonAsync(WebSocket socket)
    {
        using var cts = new CancellationTokenSource(IoTimeout);
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer.AsMemory(), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                throw new InvalidOperationException("WsTestServer: peer closed while a message was expected.");
            }

            message.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                return JsonDocument.Parse(message.ToArray());
            }
        }
    }

    /// <summary>Drains the connection until the peer starts the close handshake, then completes it.</summary>
    public static async Task WaitCloseAsync(WebSocket socket)
    {
        var buffer = new byte[4 * 1024];
        try
        {
            using var cts = new CancellationTokenSource(IoTimeout);
            while (true)
            {
                var result = await socket.ReceiveAsync(buffer.AsMemory(), cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
                    return;
                }
            }
        }
        catch (Exception)
        {
            // The peer may abort instead of closing cleanly; that ends the wait too.
        }
    }

    public static object ArticleMessage(string link) => new
    {
        action = "sendArticle",
        data = new
        {
            link,
            title = "Title " + link,
            publishDate = "2024-01-01T00:00:00Z",
            source = "example.com",
            language = "en",
        },
    };

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync(TimeSpan.FromSeconds(5));
        await _app.DisposeAsync();
    }
}
