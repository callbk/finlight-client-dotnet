# finlight client for .NET

*English | [简体中文](README.zh-CN.md) | [日本語](README.ja.md) | [한국어](README.ko.md)*

> **Breaking release.** Version 2.0.0 speaks only the finlight API v3. Code written for 1.x does not compile
> unchanged. To stay on 1.x (API v2), pin `Version="[1.0.0,2.0.0)"`. See [Migrating from 1.x](#migrating-from-1x).

.NET client for the [finlight](https://finlight.me) news API: search and look up financial news,
companies and sources over REST, stream articles in real time over WebSocket, and verify webhooks.

## Versions

| SDK version | API version | Status |
|---|---|---|
| 2.x | v3 (REST `/v3`, WebSocket `payloadVersion` 3, v3 webhooks) | current |
| 1.x | v2 | no further releases |

SDK and API versions are independent. The finlight SDKs that speak API v3: TypeScript 4.x, Python 3.x,
PHP 2.x, Go module `/v2`, .NET 2.x, Java 1.x, Ruby 1.x, Rust 0.2.

## Requirements

- .NET 8 or newer (tested on .NET 8 and .NET 10)
- `Microsoft.Extensions.Logging.Abstractions` 8.0.0+ and `Microsoft.Extensions.Primitives` 8.0.0+ (pulled in by the package)

## Installation

```bash
dotnet add package Finlight.Client --version "[2.0.0,3.0.0)"
```

## Quick start

```csharp
using Finlight;

using var client = new FinlightClient("YOUR_API_KEY");
var page = await client.Articles.SearchAsync(new SearchParams { Query = "ticker:NVDA", Limit = 10 });
foreach (var article in page.Articles)
{
    Console.WriteLine(article.Title);
}
```

Every I/O method is async, ends in `Async` and takes a `CancellationToken`. Each property of a parameter
type maps to exactly one key of the API (`OrderBy` is `orderBy`). Only the properties you set are sent:
`new SearchParams()` sends `{}`.

## Search articles

```csharp
var page = await client.Articles.SearchAsync(new SearchParams
{
    Query = "eventType:mna AND companyCountry:US",
    Languages = ["en", "de"],
    From = new DateOnly(2026, 11, 1),          // string, DateOnly, DateTimeOffset or a UTC/local DateTime
    EventTypes = [EventType.Mna],
    ExcludeSourceTypes = [SourceType.PressRelease],
    MinRelevance = 0.5,
    OrderBy = OrderBy.AvailableAt,
    Include = [IncludeBlock.Content, IncludeBlock.Companies, IncludeBlock.Events],
    Limit = 50,
});
Console.WriteLine($"{page.Pagination.HasMore} {page.Pagination.NextCursor}");
```

`SearchAllAsync` walks the pages for you. It freezes the request when you call it and only adds the
cursor on later pages. It stops after `MaxPages` pages (default 10), because every page costs quota.

```csharp
var results = client.Articles.SearchAllAsync(
    new SearchParams { Query = "ticker:AAPL", Limit = 100 },
    new SearchAllOptions { MaxPages = 5 });
await foreach (var article in results)      // or: await foreach (var page in results.Pages())
{
    Console.WriteLine($"{article.Id} {article.Title}");
}

if (results.Truncated)
{
    Console.WriteLine($"continue with Cursor = {results.NextCursor}");
}
```

Pass `Cursor = results.NextCursor` with the same parameters to continue. A result can be iterated once.
Wrong shapes (for example `Limit = 0` or `Languages = []`) throw `FinlightValidationException` before
anything is sent, also for `SearchAllAsync` and streams. Vocabulary values are not checked locally, so
new server values work without an SDK update: `EventTypes = ["new_type"]` goes out as is.

## Look up articles, companies and sources

```csharp
var article = await client.Articles.GetAsync("EU8JwpsZ8iJehuyPZjMqRz", new LookupOptions { Include = [IncludeBlock.Content] });
article = await client.Articles.GetByLinkAsync("https://www.reuters.com/...");

var companies = await client.Companies.SearchAsync(new CompanySearchParams { Q = "NVDA", Mic = "XNAS" }); // at most 10, no pages
companies = await client.Companies.GetManyAsync([4821, 1001]);
var company = await client.Companies.GetAsync(4821);   // revalidated with its ETag next time
Console.WriteLine(company?.GetCacheInfo()?.Etag);

var sources = await client.Sources.ListAsync();
```

- A miss throws `NotFoundException`; it is never an empty result.
- Articles indexed before article ids existed can only be fetched by link.
- Company lookups need the `entityAccess` entitlement (`ForbiddenException` otherwise).
- Company and source lookups cost 1 credit per request (`GetManyAsync` 1 per request, not per id), like
  an article request. A 304 revalidation and errors cost 1 credit too. They can get a 429 like any call.
- `Companies.GetAsync` keeps the last records and revalidates them with their ETag (`CompanyCacheSize`).
  With your own `new CompanyGetOptions { IfNoneMatch = etag }`, a 304 answer returns `null`.
  `GetCacheInfo()` gives `Etag`, `CacheControl` and `NotModified`.
- `CompanyList` and `SourceList` are read-only lists with a `Skipped` property; `ArticlePage.Skipped` does
  the same for search pages. Entries that cannot be read land there and are logged as warnings.

## Absent, null and empty

Some keys depend on your plan or on `include`. They have up to three states, kept apart by
`Optional<T>`:

| State | Meaning | How to check |
|---|---|---|
| absent | not requested, not entitled, or a raw stream | `article.ContentPresent` is `false` |
| `null` | requested and entitled, but no value | `ContentPresent` is `true` and `article.Content.Value` is `null` |
| value | the value | `article.Content.Value` is set |

- `Content` and `Sentiment` have all three states: use `ContentPresent` and `SentimentPresent`.
- `Companies` and `Events` are absent or a list; `[]` means the article was tagged and nothing was found.
  Use `CompaniesPresent` and `EventsPresent`. `[]` never means "not entitled".
- `EventTag.CompanyIds` is absent unless `companies` is in the same answer.
- `Source.HasContent` is absent without `fullArticleAccess`: unknown, not "no content".
- `article.Source.Type` and `Source.Type` are `null` when the source is not classified. `SourceTypes` never
  matches such articles and `ExcludeSourceTypes` keeps them.
- Timestamps stay the strings that were sent (`PublishedAt`). `PublishedAtTime` and the other `...Time`
  properties parse them strictly (RFC 3339) and return `null` for anything unreadable.
- Unknown keys from newer servers are ignored. `article.GetRawJson()` gives the JSON as received.
  `JsonSerializer.Serialize(article)` writes a model back with absent keys left out.
- Vocabularies are open `readonly record struct`s: `CompanyRole.Acquirer`, `EventType.Mna`, ... compare by
  value (`role == CompanyRole.Acquirer`), unknown values are kept as sent, and `CompanyRoleValues.Acquirer`
  is a `const string` for `switch`.

## Real-time streams

```csharp
await using var stream = client.StreamAsync(
    new StreamParams { Query = "ticker:NVDA", Language = "en", Include = [IncludeBlock.Companies, IncludeBlock.Events], IncludeUpdates = true },
    new StreamOptions { OnReconnect = info => Console.WriteLine($"reconnected {info.Reason} {info.LastSeenAt}") },
    cancellationToken);

await foreach (var item in stream)
{
    Console.WriteLine($"{item.Kind} {item.Data?.Title ?? item.Event.Value}");
}
```

- `item.Kind` is `created`, `updated` or `other` (an event this SDK does not know yet).
- `client.RawStreamAsync(new RawStreamParams { ... })` delivers articles before enrichment: no `include`,
  no content, sentiment, companies or events.
- `StreamParams` and `RawStreamParams` take `SourceTypes` and `ExcludeSourceTypes` like search
  (`ExcludeSourceTypes = [SourceType.Blog]`). They are sent only when set. An unknown value ends the stream
  with `ValidationFailedException`.
- Hooks in `StreamOptions`: `OnAdmitted(AdmittedInfo)`, `OnReconnect(ReconnectInfo)` after every new
  connection but the first, `OnError(FinlightException)` for problems that do not end the stream, and
  `OnQuotaWait(QuotaWaitInfo)`. Exceptions thrown in a hook are logged and never stop the stream.
- The stream pings every 25 s, reconnects when no pong came for 60 s, and opens a fresh connection after
  115 minutes. After each reconnect it sends the same subscribe and drops duplicates.
- It never backfills. `ReconnectInfo.LastSeenAt` is the latest `availableAt` (raw: `firstSeenAt`) you saw,
  or null when nothing arrived yet. Fill gaps with `if (info.LastSeenAt is { } since)` and
  `SearchAllAsync(new SearchParams { From = since, OrderBy = OrderBy.AvailableAt })` (raw: `OrderBy.FirstSeenAt`).
- Terminal problems end the loop with an exception: invalid key (`UnauthorizedException`), no WebSocket
  access (`ForbiddenException`), a rejected subscribe (`ValidationFailedException`), and
  `PreemptedException` when another connection took over. Other problems reconnect and go to `OnError`.
- When the monthly WebSocket quota is used up (the connect is refused, or a preempt with reason
  `quota_exceeded`), the stream does not end: it tries again every hour until the quota allows it. Each
  wait logs a warning and calls `OnQuotaWait`. Set `QuotaReconnect = false` in `FinlightConfig` to end
  the stream with the error instead; `QuotaReconnectInterval` changes the hour.
- Leaving the loop, the `CancellationToken` or `DisposeAsync()` closes the socket with 1000 and stops
  every timer. Cancellation arrives as `OperationCanceledException`. One client can run several streams;
  `client.Dispose()` closes all of them.

Limits to know:

- Your plan's connection limit is shared by enriched, raw and older SDK connections. A new connection
  while the limit is reached is refused; the SDK retries after 60 s. Set `Takeover = true` in
  `StreamOptions` to replace the oldest connection instead.
- The 115-minute renewal opens the new connection before it closes the old one. When that second
  connection is refused or fails (for example because your limit leaves no room for it and `Takeover`
  is off), the SDK closes the old connection, waits for its close and connects again at once (a short
  gap, no `OnError`). Only when that connection is refused too does the usual wait apply.
- Right after subscribing, the server sends the newest matching article once (also after every
  reconnect). It can be old.
- Live frames over 32,000 bytes are dropped by the server without notice. This can happen with
  `Include = [IncludeBlock.Content]` on long articles. Fetch them with `Articles.GetAsync`.
- `connection.admitted` arrives before the server checks the subscribe. It does not mean it was accepted.

## Webhooks

finlight signs every webhook. Verify it with the **raw** request body, before parsing anything:

```csharp
app.MapPost("/finlight-webhook", async (HttpRequest request) =>
{
    using var body = new MemoryStream();
    await request.Body.CopyToAsync(body);                // the raw bytes, never re-serialized JSON
    WebhookPayload payload;
    try
    {
        payload = Webhooks.ConstructEvent(body.ToArray(), request.Headers, webhookSecret);
    }
    catch (WebhookVerificationException)
    {
        return Results.StatusCode(400);                  // never 410
    }

    switch (payload)
    {
        case WrongPayloadVersion old:
            KeepForLater(old.Raw);                       // an old v2 body: answer 2xx anyway
            return Results.Ok();
        case WebhookEvent e when AlreadySeen(e.EventId): // deliveries can repeat: deduplicate on EventId
            return Results.Ok();
        case WebhookEvent e:
            Queue(e);                                    // answer within 5 s, work afterwards
            return Results.Ok();
        default:
            return Results.Ok();
    }
});
```

- `headers` can be ASP.NET Core `Request.Headers` (any `IEnumerable<KeyValuePair<string, StringValues>>`)
  or `HttpHeaders`. Names are matched without regard to case, also in the CGI form `HTTP_X_WEBHOOK_...`.
  `Webhooks.ConstructEvent(rawBody, signature, timestamp, secret)` takes the two header values directly;
  `Webhooks.VerifySignature(...)` only checks. The body can be `byte[]` or the received UTF-8 `string`.
- The timestamp must be within `Webhooks.DefaultTolerance` (300 s) in both directions
  (`WebhookVerifyOptions.Tolerance`, `WebhookVerifyOptions.Now` for a test clock).
- `WebhookVerificationException.Reason` says why, for example `signature_mismatch` or
  `timestamp_outside_tolerance`.
- `e.IsTest` is true for the dashboard test webhook. It is not cut by your plan.
- Never answer with 410: it disables the webhook at once. Ten non-2xx answers in a row disable it too.

Getting the raw body:

| Framework | Raw body |
|---|---|
| ASP.NET Core minimal API | `await request.Body.CopyToAsync(buffer)` (no `[FromBody]` model) |
| ASP.NET Core MVC | `Request.EnableBuffering()`, then read `Request.Body` before model binding, or bind nothing |
| Azure Functions (isolated) | `await req.Body.CopyToAsync(buffer)` on `HttpRequestData` |

## Errors and retries

All exceptions derive from `FinlightException` and carry `Kind`: `api`, `validation`, `network`, `timeout`,
`decode`, `webhook_verification` or `preempted`.

| Exception | When |
|---|---|
| `FinlightApiException` | The API answered with an error: REST, a refused stream connection, or a stream error. `Status`, `Code`, `Type`, `Title`, `Detail`, `RequestId`, `Errors` (`FieldIssue`), `ApiKind`, `Raw`. Subclasses: `ValidationFailedException`, `CursorInvalidException`, `CursorMismatchException`, `UnauthorizedException`, `ForbiddenException`, `NotFoundException`, `RateLimitedException`, `InternalServerException`. Anything else has `ApiKind` `other`. |
| `RateLimitedException` | `LimitType` is `monthly`, `burst`, `unknown` or a newer value; `RetryAfter` as `TimeSpan`. |
| `FinlightValidationException` | A request was rejected locally; `Param` names the key. |
| `FinlightNetworkException`, `FinlightTimeoutException` | Network failure or timeout. The original error is `InnerException`. |
| `FinlightDecodeException` | A response could not be read (`Path`, `Raw`). |
| `WebhookVerificationException` | A webhook failed verification (`Reason`). |
| `PreemptedException` | Another connection took over the stream (`Reason`, `NewLeaseId`). With `QuotaReconnect = false` also when the quota ran out. |

Retries: 5xx answers, network errors and timeouts are retried up to 3 times with growing waits, the
search included. 400, 401, 403, 404 and a monthly 429 are never retried: a monthly 429 fails at once,
also inside `SearchAllAsync`; only streams wait for the quota (see above). A burst 429 waits exactly
`Retry-After` (10 s without the header) and tries once more; this is on by default, does not count
against `MaxRetries`, and `RetryOnBurst = RetryOnBurst.Off` turns it off. Every call sends its own
`X-Request-Id`, the same on each retry. Cancelling the token stops a call at once, waits included, and
arrives unchanged as `OperationCanceledException`.

## Configuration

```csharp
using var client = new FinlightClient(new FinlightConfig
{
    ApiKey = "YOUR_API_KEY",
    BaseUrl = "https://api.finlight.me",   // a trailing slash is fine
    WssUrl = "wss://wss.finlight.me",      // raw streams use <WssUrl>/raw
    Timeout = TimeSpan.FromSeconds(30),    // per attempt
});
```

| Key | Default | Meaning |
|---|---|---|
| `ApiKey` | required | your API key, never empty |
| `BaseUrl` | `https://api.finlight.me` | REST base URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket URL, raw at `/raw` |
| `Timeout` | 30 s | per REST attempt |
| `TotalTimeout` | none | cap per call, retries and burst wait included |
| `MaxRetries` | 3 | retries for 5xx, network errors and timeouts |
| `RetryBaseDelay`, `RetryMaxDelay` | 0.5 s, 8 s | growing wait between retries |
| `RetryOnBurst` | `Once` | `Off` turns the burst retry off |
| `BurstWait` | 10 s | burst wait when `Retry-After` is missing |
| `MaxPages` | 10 | page cap of `SearchAllAsync` |
| `MaxResponseBytes` | 32 MiB | size limit of a REST answer |
| `CompanyCacheSize` | 256 | records kept by `Companies.GetAsync`, 0 turns it off |
| `PingInterval` | 25 s | stream ping |
| `WatchdogTimeout` | 60 s | reconnect when no pong came for this long |
| `RotationInterval` | 115 min | fresh connection before the 2 h limit |
| `ReconnectBaseDelay`, `ReconnectMaxDelay` | 1 s, 30 s | growing wait between reconnects |
| `LongBackoff` | 60 s | wait after a refusal for the connection limit or a 429 |
| `QuotaReconnect` | `true` | wait for the monthly WebSocket quota instead of ending |
| `QuotaReconnectInterval` | 1 h | wait between quota attempts |
| `SearchFailedThreshold` | 5 | `SEARCH_FAILED` in a row before `OnError` |
| `DedupeCacheSize` | 1000 | remembered deliveries per stream |
| `MaxMessageBytes` | 32 MiB | size limit of a stream message (at least 16 MiB) |
| `ConnectTimeout` | 30 s | WebSocket handshake |
| `LoggerFactory` | none (silent) | your `ILoggerFactory` |
| `TimeProvider` | `TimeProvider.System` | clock of retry waits and stream timers (for tests) |
| `HttpClient` | none | your own `HttpClient`, for REST and the WebSocket handshake |

With your own `HttpClient`, its timeouts, proxy and handlers apply (the SDK's `Timeout` does not); do not
add resilience handlers or Polly retries, and do not set a short `HttpClient.Timeout`: the SDK retries
itself and every attempt costs quota. Turn off redirects (`AllowAutoRedirect = false` on the handler): the
SDK's own handlers never follow them, because the API key header would go to the new host. The SDK never
disposes a client it did not create. If you log HTTP traffic with `IHttpClientFactory`, hide the key with
`RedactLoggedHeaders(["X-API-KEY"])`.

## Logging

The SDK logs through `ILogger` under the category `Finlight` when you pass `LoggerFactory`. Without it,
it is silent (`NullLogger`). Warnings cover skipped entries, reconnects and quota waits. The API key, the
webhook secret, signatures and full bodies never appear in logs, exception messages or `ToString()`. The
SDK never writes to the console, never changes global logging settings and installs no global handlers.

```csharp
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().AddFilter("Finlight", LogLevel.Information));
using var client = new FinlightClient(new FinlightConfig { ApiKey = key, LoggerFactory = loggerFactory });
```

## Data notes

- Old articles can carry `PublishedAt` `"1970-01-01T00:00:00.000Z"`. It is delivered as sent.
- Empty texts come as `null` (`Summary`, `Content`).
- Articles tagged before roles existed have `Role` and `Relevance` `null` and `Events` `[]`.
- `Articles.GetAsync(id)` answers 404 for articles indexed before ids existed. Use `GetByLinkAsync`.
- `AvailableAt` is `null` on older articles and always `null` on raw streams.

## Migrating from 1.x

The full guide is on [docs.finlight.me](https://docs.finlight.me/migration/v2-to-v3). The short version:

| 1.x | 2.x |
|---|---|
| `new FinlightClient(new FinlightClientOptions { ... }, httpClient)` | `new FinlightClient(new FinlightConfig { ..., HttpClient = httpClient })` |
| `client.Articles.FetchArticlesAsync(new GetArticlesParams { ... })` | `client.Articles.SearchAsync(new SearchParams { ... })`, `SearchAllAsync(...)` |
| `Page` / `PageSize` | `Limit` / `Cursor` |
| `Language` | `Languages = ["en"]` (search); streams keep `Language` |
| `IncludeContent`, `IncludeEntities` | `Include = [IncludeBlock.Content, IncludeBlock.Companies, IncludeBlock.Events]` |
| `Order = SortOrder.Desc` (closed enum) | `Order = SortOrder.Desc` (open value `"desc"`) |
| `PublishDate`, `CreatedAt`, `RevisedDate` (`DateTimeOffset`) | `PublishedAt`, `FirstSeenAt`, `RevisedAt` (wire string) plus `...Time` |
| `Images` | `Media[].Url` |
| `Source` (string) | `Source.Domain` |
| `Sentiment`, `Confidence` | `Sentiment.Value.Label`, `Sentiment.Value.Confidence` |
| `FetchArticleByLinkAsync(...)` | `GetByLinkAsync(link, options)`, new: `GetAsync(id)` |
| `client.Sources.GetSourcesAsync()` | `client.Sources.ListAsync()` |
| `client.WebSocket.StreamAsync(...)` | `await foreach (var item in client.StreamAsync(...))` |
| `client.RawWebSocket.StreamAsync(...)` | `client.RawStreamAsync(...)` |
| `FinlightBlockedException` | `FinlightApiException` (and its subclasses), `PreemptedException` |
| `FinlightWebhooks.ConstructEvent(...)` returning an article | `Webhooks.ConstructEvent(...)` returning `WebhookEvent` or `WrongPayloadVersion` |
| webhook `IsUpdate` | `e.Event == WebhookEventName.ArticleUpdated` |

Switching a webhook to v3, in four steps:

1. Deploy this SDK.
2. Answer `WrongPayloadVersion` with 2xx and keep its `Raw` JSON.
3. Switch the webhook to `payloadVersion` 3 in the dashboard.
4. Wait about 2 hours: retries queued before the switch still arrive in the old format.

1.x and 2.x cannot be loaded side by side in one application (same assembly name). Do not run 1.x and
2.x streams at the same time on one key: they share the connection limit.

## Development

```bash
dotnet test                                                  # unit and conformance tests, no network
scripts/sync-conformance.sh --from <monorepo>/shared/contracts/api-v3
scripts/sync-conformance.sh --check
python3 scripts/generate-vocabulary.py conformance/vocab.json src/Finlight.Client/Vocabulary.g.cs
python3 scripts/check-readme.py
```

`conformance/` is a copy of the contract kit with its `CONTRACT_VERSION` and sha256 `MANIFEST`;
`CONFORMANCE.md` maps every kit case to its test. The examples in `examples/` are built and run by the
tests. Live tests run only against dev and need `FINLIGHT_API_KEY`, `FINLIGHT_BASE_URL` and
`FINLIGHT_WSS_URL`: `dotnet test tests/Finlight.Client.LiveTests`. They are not part of the solution.

## License

MIT
