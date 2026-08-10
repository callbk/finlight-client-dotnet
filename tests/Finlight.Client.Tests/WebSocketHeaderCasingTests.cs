using System.Net;
using System.Net.Sockets;
using System.Text;
using Finlight.WebSockets;

namespace Finlight.Tests;

public class WebSocketHeaderCasingTests
{
    /// <summary>
    /// The server reads the auth headers case-sensitively in exact lowercase and
    /// rejects canonicalized names (X-Api-Key) with 401. This pins the raw bytes
    /// of the upgrade request against runtime changes.
    /// </summary>
    [Fact]
    public async Task Handshake_SendsHeadersInExactLowercase()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var upgradeBytes = CaptureFirstRequestAsync(listener);

            var client = new ArticleWebSocketClient(
                new FinlightClientOptions
                {
                    ApiKey = "test-key",
                    WssUrl = $"ws://127.0.0.1:{port}",
                    Timeout = TimeSpan.FromSeconds(2),
                },
                new FinlightWebSocketOptions { Takeover = true });

            using var cts = new CancellationTokenSource();
            var streamTask = Task.Run(async () =>
            {
                try
                {
                    await foreach (var _ in client.StreamAsync(new GetArticlesWebSocketParams(), cts.Token))
                    {
                    }
                }
                catch (OperationCanceledException)
                {
                }
            });

            var raw = await upgradeBytes.WaitAsync(TimeSpan.FromSeconds(10));
            cts.Cancel();
            await streamTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.Contains("\r\nx-api-key: test-key\r\n", raw);
            Assert.Contains("\r\nx-client-version: dotnet/Finlight.Client@", raw);
            Assert.Contains("\r\nx-takeover: true\r\n", raw);
            Assert.DoesNotContain("X-Api-Key", raw);
            Assert.DoesNotContain("X-API-KEY", raw);
        }
        finally
        {
            listener.Stop();
        }
    }

    private static async Task<string> CaptureFirstRequestAsync(TcpListener listener)
    {
        using var client = await listener.AcceptTcpClientAsync();
        using var stream = client.GetStream();
        var buffer = new byte[16 * 1024];
        var read = await stream.ReadAsync(buffer);
        return Encoding.ASCII.GetString(buffer, 0, read);
    }
}
