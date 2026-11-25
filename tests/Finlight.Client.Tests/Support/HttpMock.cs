using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Finlight.Tests.Support;

/// <summary>A request the mock received, after the SDK serialized it.</summary>
internal sealed record RecordedRequest(
    string Method,
    string Path,
    string RawQuery,
    IReadOnlyList<KeyValuePair<string, string>> Query,
    IReadOnlyDictionary<string, string> Headers,
    byte[] Body)
{
    public string BodyText => Encoding.UTF8.GetString(Body);

    public JsonNode? Json => Body.Length == 0 ? null : JsonNode.Parse(Body);

    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value : throw new KeyNotFoundException(name);
}

/// <summary>A scripted answer.</summary>
internal sealed record MockResponse(int Status, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public static MockResponse Json(int status, string json, string contentType = "application/json; charset=utf-8")
        => new(status, new Dictionary<string, string> { ["content-type"] = contentType }, Encoding.UTF8.GetBytes(json));

    /// <summary>The <c>response</c> object of a kit fixture.</summary>
    public static MockResponse FromFixture(JsonNode response)
    {
        var status = response["status"]!.GetValue<int>();
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (response["headers"] is JsonObject h)
        {
            foreach (var (name, value) in h)
            {
                headers[name] = value!.GetValue<string>();
            }
        }

        var body = response["body"];
        byte[] bytes = body switch
        {
            null => [],
            JsonValue v when v.TryGetValue<string>(out var text) => Encoding.UTF8.GetBytes(text),
            _ => Encoding.UTF8.GetBytes(body.ToJsonString()),
        };
        return new MockResponse(status, headers, bytes);
    }
}

/// <summary>In-process HTTP server on 127.0.0.1 with scripted answers (R-TEST-1: real bytes on the wire).</summary>
internal sealed class HttpMock : IAsyncDisposable
{
    private readonly WebApplication _app;
    private readonly ConcurrentQueue<MockResponse> _script = new();
    private readonly List<RecordedRequest> _requests = [];

    private HttpMock(WebApplication app)
    {
        _app = app;
    }

    public string Url { get; private set; } = string.Empty;

    /// <summary>Answer used when the script is empty.</summary>
    public MockResponse Fallback { get; set; } = MockResponse.Json(200, "{}");

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_requests)
            {
                return [.. _requests];
            }
        }
    }

    public static async Task<HttpMock> StartAsync()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        var app = builder.Build();
        var mock = new HttpMock(app);
        app.Run(mock.HandleAsync);
        await app.StartAsync();
        mock.Url = app.Urls.First();
        return mock;
    }

    public void Enqueue(params MockResponse[] responses)
    {
        foreach (var response in responses)
        {
            _script.Enqueue(response);
        }
    }

    private async Task HandleAsync(HttpContext context)
    {
        using var body = new MemoryStream();
        await context.Request.Body.CopyToAsync(body);
        var raw = context.Request.QueryString.HasValue ? context.Request.QueryString.Value!.TrimStart('?') : string.Empty;
        var query = new List<KeyValuePair<string, string>>();
        foreach (var part in raw.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = part.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? part : part[..eq];
            var value = eq < 0 ? string.Empty : part[(eq + 1)..];
            query.Add(new(Uri.UnescapeDataString(key), Uri.UnescapeDataString(value)));
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, values) in context.Request.Headers)
        {
            headers[name] = values.ToString();
        }

        lock (_requests)
        {
            _requests.Add(new RecordedRequest(
                context.Request.Method,
                context.Request.Path.Value ?? "/",
                raw,
                query,
                headers,
                body.ToArray()));
        }

        var response = _script.TryDequeue(out var next) ? next : Fallback;
        context.Response.StatusCode = response.Status;
        foreach (var (name, value) in response.Headers)
        {
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.ContentType = value;
            }
            else
            {
                context.Response.Headers[name] = value;
            }
        }

        if (response.Body.Length > 0 && response.Status != 304)
        {
            await context.Response.Body.WriteAsync(response.Body);
        }
    }

    public async ValueTask DisposeAsync()
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await _app.StopAsync(cts.Token);
        await _app.DisposeAsync();
    }
}
