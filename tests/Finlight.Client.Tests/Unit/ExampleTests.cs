using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Unit;

/// <summary>Runs the 5 standard examples (built with the tests) against the local mocks (R-TEST-1).</summary>
public sealed class ExampleTests
{
    private const string Page = """{"articles":[{"id":"EU8JwpsZ8iJehuyPZjMqRz","link":"https://news.example.com/a","source":{"domain":"news.example.com","type":null},"title":"Example title","publishedAt":"2026-11-23T10:00:00.000Z","companies":[{"companyId":900001,"name":"Example Robotics Inc."}],"events":[]}],"pagination":{"limit":10,"returned":1,"hasMore":false,"nextCursor":null}}""";

    [Fact]
    public async Task Search()
    {
        await using var mock = await HttpMock.StartAsync();
        mock.Enqueue(MockResponse.Json(200, Page));
        var (code, output) = await RunAsync("search", Env(mock.Url));
        Assert.Equal(0, code);
        Assert.Contains("Example title", output, StringComparison.Ordinal);
        Assert.Contains("Example Robotics Inc.", output, StringComparison.Ordinal);
        Assert.Contains("hasMore: False", output, StringComparison.Ordinal);
        var body = Assert.Single(mock.Requests).Json!;
        Assert.Equal("""{"include":["companies","events"],"languages":["en"],"limit":10,"query":"ticker:NVDA"}""", Kit.Canonical(body));

        mock.Enqueue(MockResponse.Json(401, """{"type":"t","title":"Unauthorized","status":401,"detail":"Unauthorized","code":"UNAUTHORIZED","requestId":"req-x"}""", "application/problem+json"));
        (code, output) = await RunAsync("search", Env(mock.Url));
        Assert.Equal(1, code);
        Assert.Contains("401 UNAUTHORIZED (requestId req-x)", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SearchAll()
    {
        await using var mock = await HttpMock.StartAsync();
        for (var i = 0; i < 3; i++)
        {
            mock.Enqueue(MockResponse.Json(200, Page.Replace("\"hasMore\":false,\"nextCursor\":null", $"\"hasMore\":true,\"nextCursor\":\"cur{i}\"", StringComparison.Ordinal)));
        }

        var (code, output) = await RunAsync("search-all", Env(mock.Url));
        Assert.Equal(0, code);
        Assert.Contains("articles: 3, pages: 3", output, StringComparison.Ordinal);
        Assert.Contains("Cursor = \"cur2\"", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Lookup()
    {
        await using var mock = await HttpMock.StartAsync();
        var article = JsonNode.Parse(Page)!["articles"]![0]!.ToJsonString();
        var company = Kit.Obj("fixtures/requests/companies-get.json")["response"]!;
        mock.Enqueue(
            MockResponse.Json(200, Page),
            MockResponse.Json(200, $$"""{"article":{{article.Replace("\"events\":[]", "\"events\":[],\"content\":null", StringComparison.Ordinal)}}}"""),
            MockResponse.Json(200, $$"""{"article":{{article}}}"""),
            MockResponse.Json(404, """{"type":"t","title":"Not found","status":404,"detail":"No article with id 'AAAAAAAAAAAAAAAAAAAAAA'.","code":"NOT_FOUND","requestId":"r"}""", "application/problem+json"),
            MockResponse.FromFixture(Kit.Obj("fixtures/requests/companies-search.json")["response"]!),
            MockResponse.FromFixture(company),
            MockResponse.FromFixture(Kit.Obj("fixtures/requests/sources-list-full.json")["response"]!));
        var (code, output) = await RunAsync("lookup", Env(mock.Url));
        Assert.Equal(0, code);
        Assert.Contains("content: none", output, StringComparison.Ordinal);
        Assert.Contains("404: No article with id", output, StringComparison.Ordinal);
        Assert.Contains("etag: \"80b88d6b9db35078140932ce62ec53d1b7b368bc\"", output, StringComparison.Ordinal);
        Assert.Contains("sources: ", output, StringComparison.Ordinal);
        Assert.Equal(7, mock.Requests.Count);
    }

    [Fact]
    public async Task Stream()
    {
        await using var ws = await WsMock.StartAsync();
        var env = Env("http://127.0.0.1:9");
        env["FINLIGHT_WSS_URL"] = ws.Url;
        var run = RunAsync("stream", env);
        var conn = (await ws.NextDialAsync("the example's dial")).Conn!;
        var subscribe = (await conn.NextAsync("the example's subscribe"))!.AsObject();
        Assert.Equal("ticker:NVDA", subscribe["query"]!.GetValue<string>());
        Assert.True(subscribe["includeUpdates"]!.GetValue<bool>());
        await conn.SendAsync("""{"event":"connection.admitted","payloadVersion":3,"data":{"leaseId":"lease-1","serverNow":1,"clientNonce":"c"}}""");
        await conn.SendAsync("""{"event":"connection.draining","payloadVersion":3,"data":{}}""");
        var data = JsonNode.Parse(Page)!["articles"]![0]!;
        for (var i = 0; i < 6; i++)
        {
            data["id"] = "id" + i;
            await conn.SendAsync(new JsonObject { ["event"] = "article.created", ["payloadVersion"] = 3, ["data"] = data.DeepClone() });
        }

        var (code, output) = await run;
        Assert.Equal(0, code);
        Assert.Contains("admitted (lease lease-1)", output, StringComparison.Ordinal);
        Assert.Equal(5, output.Split('\n').Count(l => l.StartsWith("created: ", StringComparison.Ordinal)));
        await conn.Closed.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1000, conn.CloseStatus);
    }

    [Fact]
    public async Task WebhookEndpoint()
    {
        const string secret = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        var env = new Dictionary<string, string> { ["FINLIGHT_WEBHOOK_SECRET"] = secret, ["PORT"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture) };
        using var process = Start("webhook-endpoint", env, out var output);
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}") };
            await Wait.ForAsync(() => output.ToString().Contains("listening", StringComparison.Ordinal), "the endpoint to start", 30000);
            var timestamp = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
            var v3 = Kit.Obj("fixtures/webhook/created-enriched.json")["request"]!["rawBody"]!.GetValue<string>();
            var v2 = Kit.Obj("fixtures/webhook/v2-body.json")["request"]!["rawBody"]!.GetValue<string>();

            async Task<HttpStatusCode> PostAsync(string body, string? signature = null)
            {
                for (var attempt = 0; ; attempt++)
                {
                    using var request = new HttpRequestMessage(HttpMethod.Post, "/finlight-webhook") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
                    request.Headers.Add("X-Webhook-Signature", signature ?? "sha256=" + Convert.ToHexString(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(timestamp + "." + body))).ToLowerInvariant());
                    request.Headers.Add("X-Webhook-Timestamp", timestamp);
                    try
                    {
                        return (await http.SendAsync(request)).StatusCode;
                    }
                    catch (HttpRequestException) when (attempt < 50)
                    {
                        await Task.Delay(200);
                    }
                }
            }

            Assert.Equal(HttpStatusCode.OK, await PostAsync(v3));
            Assert.Equal(HttpStatusCode.OK, await PostAsync(v3));
            Assert.Equal(HttpStatusCode.OK, await PostAsync(v2));
            Assert.Equal(HttpStatusCode.BadRequest, await PostAsync(v3, "sha256=" + new string('0', 64)));
            await Wait.ForAsync(() => output.ToString().Contains("article.created evt_", StringComparison.Ordinal), "the background work", 10000);
            var text = output.ToString();
            Assert.Contains("duplicate evt_", text, StringComparison.Ordinal);
            Assert.Contains("old payload version", text, StringComparison.Ordinal);
            Assert.Contains("rejected: signature_mismatch", text, StringComparison.Ordinal);
        }
        finally
        {
            process.Kill(entireProcessTree: true);
            await process.WaitForExitAsync();
        }
    }

    private static Dictionary<string, string> Env(string baseUrl) => new()
    {
        ["FINLIGHT_API_KEY"] = "sk_example_test_key",
        ["FINLIGHT_BASE_URL"] = baseUrl,
        ["FINLIGHT_WSS_URL"] = "ws://127.0.0.1:9",
    };

    private static async Task<(int Code, string Output)> RunAsync(string name, Dictionary<string, string> env)
    {
        using var process = Start(name, env, out var output);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            await process.WaitForExitAsync(cts.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"example {name} did not finish: {output}");
        }

        process.WaitForExit();
        return (process.ExitCode, output.ToString());
    }

    private static Process Start(string name, Dictionary<string, string> env, out StringBuilder output)
    {
        var testDir = new DirectoryInfo(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar));
        var configuration = testDir.Parent!.Name;
        var dll = Path.Combine(Kit.RepoRoot, "examples", name, "bin", configuration, "net8.0", name + ".dll");
        Assert.True(File.Exists(dll), $"example not built: {dll}");
        var info = new ProcessStartInfo("dotnet", [dll])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        info.Environment.Remove("FINLIGHT_API_KEY");
        info.Environment["DOTNET_ROLL_FORWARD"] = "Major";
        foreach (var (key, value) in env)
        {
            info.Environment[key] = value;
        }

        var buffer = new StringBuilder();
        var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) { lock (buffer) { buffer.AppendLine(e.Data); } } };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) { lock (buffer) { buffer.AppendLine(e.Data); } } };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        output = buffer;
        return process;
    }
}
