# finlight .NET Client

Official .NET client for the [finlight.me](https://finlight.me) financial news API.
Full API documentation: [docs.finlight.me](https://docs.finlight.me)

## Features

- 🔎 **Article search** — full-text and field queries over enriched financial news
- ⚡ **Real-time streaming** — enhanced and raw WebSocket feeds as `IAsyncEnumerable`
- 🔁 **Resilient by default** — retries with backoff, automatic reconnects, proactive connection rotation
- 🔐 **Webhook verification** — HMAC-SHA256 signature checks with replay protection
- 🪶 **Lightweight** — one dependency (`Microsoft.Extensions.Logging.Abstractions`), nullable-annotated, fully documented API

## Installation

```bash
dotnet add package Finlight.Client
```

Requires .NET 8 or later.

## Quick Start

```csharp
using Finlight;

using var client = new FinlightClient("your-api-key");

var response = await client.Articles.FetchArticlesAsync(new GetArticlesParams
{
    Query = "(ticker:AAPL OR ticker:NVDA) AND NOT source:www.reuters.com",
    PageSize = 20,
});

foreach (var article in response.Articles)
{
    Console.WriteLine($"{article.PublishDate:u} [{article.Sentiment}] {article.Title}");
}
```

## REST API

### Search articles

```csharp
var response = await client.Articles.FetchArticlesAsync(new GetArticlesParams
{
    Query = "artificial intelligence",
    From = "2024-01-01",
    To = "2024-02-01",
    Language = "en",
    OrderBy = ArticleOrderBy.PublishDate,
    Order = SortOrder.Desc,
    PageSize = 100,
    Page = 1,
    IncludeContent = true,
    IncludeEntities = true,
    Categories = [Category.Markets, Category.Technology],
});
```

The API reports no total count; advance `Page` until a short or empty page comes back.

### Fetch a single article by URL

```csharp
var article = await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams
{
    Link = "https://www.example.com/some-article",
    IncludeContent = true,
});
```

### List sources

```csharp
var sources = await client.Sources.GetSourcesAsync();
```

## WebSocket Streaming

### Enhanced stream

Enriched articles (sentiment, entities, content). Duplicates within the last
10 deliveries are suppressed.

```csharp
await foreach (var article in client.WebSocket.StreamAsync(new GetArticlesWebSocketParams
{
    Query = "ticker:NVDA",
    IncludeContent = true,
}, cancellationToken))
{
    Console.WriteLine($"{article.Source}: {article.Title}");
}
```

### Raw stream

Unenriched articles with lower latency. The query language is limited to
`source:`, `title:`, and `summary:` fields.

```csharp
await foreach (var article in client.RawWebSocket.StreamAsync(new GetRawArticlesWebSocketParams
{
    Query = "title:earnings",
}, cancellationToken))
{
    Console.WriteLine($"{article.Source}: {article.Title}");
}
```

### Streaming semantics

- Reconnects (exponential backoff from 500ms to 10s, rate-limit waits, proactive
  rotation before the server's 2-hour connection cap) are handled internally.
- The stream ends **normally** when the server preempts this client because
  another connection took over the slot (see `Takeover`).
- `FinlightBlockedException` is thrown when the server permanently rejects the
  connection — reconnecting will not help.
- Cancel the token or `break` out of the loop to stop; breaking closes the
  connection cleanly.
- One active stream per client instance; a concurrent second stream throws
  `InvalidOperationException`.

### Custom WebSocket options

```csharp
using Finlight.WebSockets;

var ws = new ArticleWebSocketClient(
    new FinlightClientOptions { ApiKey = "your-api-key" },
    new FinlightWebSocketOptions
    {
        Takeover = true, // take over the connection slot from another client
        OnClose = (code, reason) => Console.WriteLine($"closed: {code} {reason}"),
    });
```

| Option | Default | Description |
| --- | --- | --- |
| `PingInterval` | 25s | Application-level ping cadence |
| `PongTimeout` | 60s | Force reconnect when no pong arrives |
| `BaseReconnectDelay` | 500ms | First reconnect backoff |
| `MaxReconnectDelay` | 10s | Backoff cap |
| `ConnectionLifetime` | 115min | Proactive rotation, under the 2h server cap |
| `Takeover` | `false` | Take over an existing connection for the same key |
| `OnClose` | – | Callback invoked with (closeCode, reason) |

## Webhooks

Verify inbound webhooks with the raw, unmodified request body:

```csharp
app.MapPost("/webhooks/finlight", async (HttpRequest request) =>
{
    using var reader = new StreamReader(request.Body);
    var rawBody = await reader.ReadToEndAsync();

    try
    {
        var article = FinlightWebhooks.ConstructEvent(
            rawBody,
            request.Headers["X-Webhook-Signature"]!,
            endpointSecret: "your-webhook-secret",
            request.Headers["X-Webhook-Timestamp"]);
        // handle article ...
        return Results.Ok();
    }
    catch (FinlightWebhookVerificationException)
    {
        return Results.Unauthorized();
    }
});
```

The signature is an HMAC-SHA256 over `"{timestamp}.{body}"` (or the body alone
when no timestamp header is present), compared in constant time, with a
5-minute replay tolerance.

## Configuration

| Option | Default | Description |
| --- | --- | --- |
| `ApiKey` | – (required) | Your finlight API key |
| `BaseUrl` | `https://api.finlight.me` | REST base URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket base URL |
| `Timeout` | 5s | Per-attempt timeout (REST and WebSocket handshake) |
| `RetryCount` | 3 | Total REST attempts |
| `TimeProvider` | system | Clock override for tests |

### Dependency injection

The client works with `IHttpClientFactory` and never disposes a caller-owned
`HttpClient`:

```csharp
services.AddHttpClient("finlight", http => http.Timeout = Timeout.InfiniteTimeSpan);
services.AddSingleton(sp => new FinlightClient(
    new FinlightClientOptions { ApiKey = configuration["Finlight:ApiKey"]! },
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("finlight"),
    sp.GetService<ILoggerFactory>()));
```

## Logging

Pass an `ILoggerFactory` to get structured logs (connection lifecycle, retries,
protocol events). Without one, the client is silent.

## Error Handling

| Exception | Meaning |
| --- | --- |
| `FinlightApiException` | Non-2xx REST response after retries; carries `StatusCode`, `ReasonPhrase`, `Body` |
| `FinlightBlockedException` | WebSocket permanently rejected (close code 1008) |
| `FinlightWebhookVerificationException` | Webhook signature/timestamp/payload validation failed |
| `TimeoutException` | A single REST attempt exceeded `Timeout` |

Retries: statuses 429, 500, 502, 503, and 504 are retried up to `RetryCount`
total attempts with exponential backoff (500ms · 2^(attempt−1)).

## Testing

```bash
dotnet test                            # unit tests (offline)
FINLIGHT_API_KEY=... dotnet test       # + integration tests against the live API
```

## License

[MIT](LICENSE)

## Support

- 📖 [Documentation](https://docs.finlight.me)
- 📧 info@finlight.me
- 🐛 [GitHub Issues](https://github.com/callbk/finlight-client-dotnet/issues)
- 🌐 [finlight.me](https://finlight.me)
