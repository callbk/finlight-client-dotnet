# finlight client for .NET

*[English](README.md) | 简体中文 | [日本語](README.ja.md) | [한국어](README.ko.md)*

> 本文是 [README.md](README.md) 的译文，更新可能滞后。内容不一致时以英文版为准。

> **Breaking release.** 2.0.0 版只支持 finlight API v3。为 1.x 编写的代码无法直接编译。
> 如需继续使用 1.x（API v2），请固定 `Version="[1.0.0,2.0.0)"`。参见 [从 1.x 迁移](#从-1x-迁移)。

[finlight](https://finlight.me) 新闻 API 的 .NET 客户端：通过 REST 搜索和查询金融新闻、公司和来源，
通过 WebSocket 实时接收文章，并验证 Webhook。

## 版本

| SDK version | API version | Status |
|---|---|---|
| 2.x | v3（REST `/v3`、WebSocket `payloadVersion` 3、v3 Webhook） | 当前 |
| 1.x | v2 | 不再发布 |

SDK 版本与 API 版本相互独立。支持 API v3 的 finlight SDK：TypeScript 4.x、Python 3.x、PHP 2.x、
Go 模块 `/v2`、.NET 2.x、Java 1.x、Ruby 1.x、Rust 0.2。

## 运行要求

- .NET 8 或更高版本（已在 .NET 8 和 .NET 10 上测试）
- `Microsoft.Extensions.Logging.Abstractions` 8.0.0+ 和 `Microsoft.Extensions.Primitives` 8.0.0+（随包引入）

## 安装

```bash
dotnet add package Finlight.Client --version "[2.0.0,3.0.0)"
```

## 快速开始

```csharp
using Finlight;

using var client = new FinlightClient("YOUR_API_KEY");
var page = await client.Articles.SearchAsync(new SearchParams { Query = "ticker:NVDA", Limit = 10 });
foreach (var article in page.Articles)
{
    Console.WriteLine(article.Title);
}
```

所有 I/O 方法都是异步的，以 `Async` 结尾并接受 `CancellationToken`。参数类型的每个属性对应 API 的一个键
（`OrderBy` 即 `orderBy`）。只发送你设置的属性：`new SearchParams()` 发送 `{}`。

## 搜索文章

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

`SearchAllAsync` 会自动翻页。调用时请求即被冻结，之后的页面只添加 cursor。由于每页都消耗配额，
它在 `MaxPages` 页（默认 10）后停止。

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

使用相同参数并设置 `Cursor = results.NextCursor` 即可继续。结果只能遍历一次。形式错误（例如 `Limit = 0`
或 `Languages = []`）会在发送前抛出 `FinlightValidationException`，`SearchAllAsync` 和流也一样。词汇值不在本地
校验，因此服务器的新值无需更新 SDK：`EventTypes = ["new_type"]` 原样发送。

## 查询文章、公司和来源

```csharp
var article = await client.Articles.GetAsync("EU8JwpsZ8iJehuyPZjMqRz", new LookupOptions { Include = [IncludeBlock.Content] });
article = await client.Articles.GetByLinkAsync("https://www.reuters.com/...");

var companies = await client.Companies.SearchAsync(new CompanySearchParams { Q = "NVDA", Mic = "XNAS" }); // at most 10, no pages
companies = await client.Companies.GetManyAsync([4821, 1001]);
var company = await client.Companies.GetAsync(4821);   // revalidated with its ETag next time
Console.WriteLine(company?.GetCacheInfo()?.Etag);

var sources = await client.Sources.ListAsync();
```

- 未找到时抛出 `NotFoundException`，绝不是空结果。
- 在文章 ID 出现之前索引的文章只能按链接获取。
- 公司查询需要 `entityAccess` 权限（否则为 `ForbiddenException`）。
- 公司和来源查询与文章请求一样，每个请求消耗 1 个额度（`GetManyAsync` 按请求计 1 个，不按 ID 计）。
  304 重新验证和错误也消耗 1 个额度。与其他调用一样，可能收到 429。
- `Companies.GetAsync` 会保留最近的记录并用 ETag 重新验证（`CompanyCacheSize`）。使用你自己的
  `new CompanyGetOptions { IfNoneMatch = etag }` 时，304 应答返回 `null`。`GetCacheInfo()` 提供 `Etag`、
  `CacheControl` 和 `NotModified`。
- `CompanyList` 和 `SourceList` 是带 `Skipped` 属性的只读列表；`ArticlePage.Skipped` 对搜索页同理。
  无法读取的条目会放在那里并记录警告。

## 缺失、null 与空

有些键取决于你的套餐或 `include`。它们最多有三种状态，由 `Optional<T>` 区分：

| 状态 | 含义 | 检查方式 |
|---|---|---|
| 缺失 | 未请求、无权限或 raw 流 | `article.ContentPresent` 为 `false` |
| `null` | 已请求且有权限，但没有值 | `ContentPresent` 为 `true` 且 `article.Content.Value` 为 `null` |
| 值 | 该值 | `article.Content.Value` 已设置 |

- `Content` 和 `Sentiment` 有全部三种状态：使用 `ContentPresent` 和 `SentimentPresent`。
- `Companies` 和 `Events` 要么缺失，要么是列表；`[]` 表示已打标签但没有找到任何内容。使用
  `CompaniesPresent` 和 `EventsPresent`。`[]` 从不表示"无权限"。
- 除非同一应答中包含 `companies`，否则 `EventTag.CompanyIds` 缺失。
- 没有 `fullArticleAccess` 时 `Source.HasContent` 缺失：表示未知，而不是"没有内容"。
- 来源未分类时，`article.Source.Type` 和 `Source.Type` 为 `null`。`SourceTypes` 不会匹配这类文章，`ExcludeSourceTypes` 会保留它们。
- 时间戳保持发送时的字符串（`PublishedAt`）。`PublishedAtTime` 等 `...Time` 属性严格按 RFC 3339 解析，
  无法读取时返回 `null`。
- 新服务器的未知键会被忽略。`article.GetRawJson()` 返回收到的原始 JSON。`JsonSerializer.Serialize(article)`
  写回模型时缺失的键保持缺失。
- 词汇是开放的 `readonly record struct`：`CompanyRole.Acquirer`、`EventType.Mna` 等按值比较，未知值原样保留，
  `CompanyRoleValues.Acquirer` 是可用于 `switch` 的 `const string`。

## 实时流

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

- `item.Kind` 为 `created`、`updated` 或 `other`（本 SDK 尚不认识的事件）。
- `client.RawStreamAsync(new RawStreamParams { ... })` 提供富化之前的文章：没有 `include`，没有内容、情感、
  公司或事件。
- `StreamParams` 和 `RawStreamParams` 与搜索一样接受 `SourceTypes` 和 `ExcludeSourceTypes`
  （`ExcludeSourceTypes = [SourceType.Blog]`）。只有设置时才会发送。未知的值会以 `ValidationFailedException` 结束流。
- `StreamOptions` 中的钩子：`OnAdmitted(AdmittedInfo)`、除第一次外每次新连接后的 `OnReconnect(ReconnectInfo)`、
  不会结束流的问题的 `OnError(FinlightException)`，以及 `OnQuotaWait(QuotaWaitInfo)`。钩子中抛出的异常会被记录，
  不会停止流。
- 流每 25 秒发送 ping，60 秒内没有 pong 就重新连接，115 分钟后打开新连接。每次重连后发送相同的订阅并丢弃重复项。
- 它从不补数据。`ReconnectInfo.LastSeenAt` 是你看到的最新 `availableAt`（raw：`firstSeenAt`），还没有收到任何内容时为 null。
  用 `if (info.LastSeenAt is { } since)` 和
  `SearchAllAsync(new SearchParams { From = since, OrderBy = OrderBy.AvailableAt })` 填补空缺
  （raw：`OrderBy.FirstSeenAt`）。
- 终止性问题会以异常结束循环：无效密钥（`UnauthorizedException`）、无 WebSocket 权限（`ForbiddenException`）、
  订阅被拒（`ValidationFailedException`），以及另一个连接接管时的 `PreemptedException`。其他问题会重连并交给 `OnError`。
- 每月 WebSocket 配额用尽时（连接被拒，或原因为 `quota_exceeded` 的抢占），流不会结束：它每小时重试一次，直到配额允许。
  每次等待都会记录警告并调用 `OnQuotaWait`。在 `FinlightConfig` 中设置 `QuotaReconnect = false` 则改为以错误结束流；
  `QuotaReconnectInterval` 可改变一小时的间隔。
- 离开循环、`CancellationToken` 或 `DisposeAsync()` 会以 1000 关闭套接字并停止所有计时器。取消以
  `OperationCanceledException` 到达。一个客户端可以同时运行多个流；`client.Dispose()` 会关闭所有流。

需要了解的限制：

- 套餐的连接上限由 enriched、raw 和旧版 SDK 的连接共享。达到上限时新连接会被拒绝；SDK 在 60 秒后重试。
  在 `StreamOptions` 中设置 `Takeover = true` 可改为替换最旧的连接。
- 115 分钟的连接更新会先打开新连接，再关闭旧连接。如果第二个连接被拒绝或失败（例如连接上限容不下它且 `Takeover` 关闭），
  SDK 会关闭旧连接，等它关闭后立即再次连接（短暂空档，不调用 `OnError`）。只有这次连接也被拒绝时，才按通常方式等待。
- 订阅后服务器会立即发送一次最新的匹配文章（每次重连后也会）。它可能是旧文章。
- 超过 32,000 字节的实时帧会被服务器静默丢弃。长文章加 `Include = [IncludeBlock.Content]` 时可能发生。
  请用 `Articles.GetAsync` 获取。
- `connection.admitted` 在服务器检查订阅之前到达，并不表示订阅已被接受。

## Webhook

finlight 会对每个 Webhook 签名。请在解析之前用**原始**请求体验证：

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

- `headers` 可以是 ASP.NET Core 的 `Request.Headers`（任何 `IEnumerable<KeyValuePair<string, StringValues>>`）
  或 `HttpHeaders`。名称不区分大小写匹配，也支持 CGI 形式 `HTTP_X_WEBHOOK_...`。
  `Webhooks.ConstructEvent(rawBody, signature, timestamp, secret)` 直接接受两个头的值；`Webhooks.VerifySignature(...)`
  只做检查。请求体可以是 `byte[]` 或收到的 UTF-8 `string`。
- 时间戳必须在双向 `Webhooks.DefaultTolerance`（300 秒）之内（`WebhookVerifyOptions.Tolerance`，测试时钟用
  `WebhookVerifyOptions.Now`）。
- `WebhookVerificationException.Reason` 说明原因，例如 `signature_mismatch` 或 `timestamp_outside_tolerance`。
- 控制台测试 Webhook 的 `e.IsTest` 为 true。它不受套餐限制。
- 切勿用 410 应答：它会立即停用 Webhook。连续 10 次非 2xx 应答也会停用。

获取原始请求体：

| 框架 | 原始请求体 |
|---|---|
| ASP.NET Core minimal API | `await request.Body.CopyToAsync(buffer)`（不用 `[FromBody]` 模型） |
| ASP.NET Core MVC | `Request.EnableBuffering()`，然后在模型绑定前读取 `Request.Body`，或不绑定任何内容 |
| Azure Functions（isolated） | 在 `HttpRequestData` 上 `await req.Body.CopyToAsync(buffer)` |

## 错误与重试

所有异常都继承自 `FinlightException` 并带有 `Kind`：`api`、`validation`、`network`、`timeout`、`decode`、
`webhook_verification` 或 `preempted`。

| 异常 | 时机 |
|---|---|
| `FinlightApiException` | API 返回错误：REST、被拒绝的流连接或流错误。`Status`、`Code`、`Type`、`Title`、`Detail`、`RequestId`、`Errors`（`FieldIssue`）、`ApiKind`、`Raw`。子类：`ValidationFailedException`、`CursorInvalidException`、`CursorMismatchException`、`UnauthorizedException`、`ForbiddenException`、`NotFoundException`、`RateLimitedException`、`InternalServerException`。其他情况 `ApiKind` 为 `other`。 |
| `RateLimitedException` | `LimitType` 为 `monthly`、`burst`、`unknown` 或更新的值；`RetryAfter` 为 `TimeSpan`。 |
| `FinlightValidationException` | 请求在本地被拒绝；`Param` 指出键名。 |
| `FinlightNetworkException`、`FinlightTimeoutException` | 网络故障或超时。原始错误在 `InnerException`。 |
| `FinlightDecodeException` | 无法读取应答（`Path`、`Raw`）。 |
| `WebhookVerificationException` | Webhook 验证失败（`Reason`）。 |
| `PreemptedException` | 另一个连接接管了流（`Reason`、`NewLeaseId`）。`QuotaReconnect = false` 时配额用尽也会如此。 |

重试：5xx、网络错误和超时最多重试 3 次，等待时间递增，搜索也包括在内。400、401、403、404 和每月 429 从不重试：
每月 429 立即失败，在 `SearchAllAsync` 中也是如此；只有流会等待配额（见上文）。突发 429 精确等待 `Retry-After`
（没有该头时为 10 秒）后再试一次；默认开启，不计入 `MaxRetries`，`RetryOnBurst = RetryOnBurst.Off` 可关闭。
每次调用发送自己的 `X-Request-Id`，重试时保持相同。取消令牌会立即停止调用（包括等待），并以
`OperationCanceledException` 原样到达。

## 配置

```csharp
using var client = new FinlightClient(new FinlightConfig
{
    ApiKey = "YOUR_API_KEY",
    BaseUrl = "https://api.finlight.me",   // a trailing slash is fine
    WssUrl = "wss://wss.finlight.me",      // raw streams use <WssUrl>/raw
    Timeout = TimeSpan.FromSeconds(30),    // per attempt
});
```

| 键 | 默认值 | 含义 |
|---|---|---|
| `ApiKey` | 必填 | 你的 API 密钥，不能为空 |
| `BaseUrl` | `https://api.finlight.me` | REST 基础 URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket URL，raw 位于 `/raw` |
| `Timeout` | 30 秒 | 每次 REST 尝试 |
| `TotalTimeout` | 无 | 每次调用的上限，含重试和突发等待 |
| `MaxRetries` | 3 | 5xx、网络错误和超时的重试次数 |
| `RetryBaseDelay`、`RetryMaxDelay` | 0.5 秒、8 秒 | 重试之间递增的等待 |
| `RetryOnBurst` | `Once` | `Off` 关闭突发重试 |
| `BurstWait` | 10 秒 | 缺少 `Retry-After` 时的突发等待 |
| `MaxPages` | 10 | `SearchAllAsync` 的页数上限 |
| `MaxResponseBytes` | 32 MiB | REST 应答的大小上限 |
| `CompanyCacheSize` | 256 | `Companies.GetAsync` 保留的记录数，0 表示关闭 |
| `PingInterval` | 25 秒 | 流的 ping |
| `WatchdogTimeout` | 60 秒 | 这么久没有 pong 就重连 |
| `RotationInterval` | 115 分钟 | 在 2 小时上限前建立新连接 |
| `ReconnectBaseDelay`、`ReconnectMaxDelay` | 1 秒、30 秒 | 重连之间递增的等待 |
| `LongBackoff` | 60 秒 | 因连接上限或 429 被拒后的等待 |
| `QuotaReconnect` | `true` | 等待每月 WebSocket 配额而不是结束 |
| `QuotaReconnectInterval` | 1 小时 | 配额尝试之间的等待 |
| `SearchFailedThreshold` | 5 | 连续多少次 `SEARCH_FAILED` 后交给 `OnError` |
| `DedupeCacheSize` | 1000 | 每个流记住的投递数 |
| `MaxMessageBytes` | 32 MiB | 流消息的大小上限（至少 16 MiB） |
| `ConnectTimeout` | 30 秒 | WebSocket 握手 |
| `LoggerFactory` | 无（静默） | 你的 `ILoggerFactory` |
| `TimeProvider` | `TimeProvider.System` | 重试等待和流计时器的时钟（用于测试） |
| `HttpClient` | 无 | 你自己的 `HttpClient`，用于 REST 和 WebSocket 握手 |

使用你自己的 `HttpClient` 时，适用它的超时、代理和处理程序（SDK 的 `Timeout` 不适用）；不要添加弹性处理程序或
Polly 重试，也不要设置很短的 `HttpClient.Timeout`：SDK 自己会重试，每次尝试都消耗配额。请关闭重定向（在 handler 上设置
`AllowAutoRedirect = false`）：SDK 自己的 handler 从不跟随重定向，因为 API 密钥头会被发到新的主机。SDK 从不释放不是它创建的客户端。
如果用 `IHttpClientFactory` 记录 HTTP 流量，请用 `RedactLoggedHeaders(["X-API-KEY"])` 隐藏密钥。

## 日志

传入 `LoggerFactory` 时，SDK 通过 `ILogger` 以类别 `Finlight` 记录日志。没有它时保持静默（`NullLogger`）。
警告涵盖跳过的条目、重连和配额等待。API 密钥、Webhook 密钥、签名和完整请求体从不出现在日志、异常消息或
`ToString()` 中。SDK 从不写入控制台，从不更改全局日志设置，也不安装全局处理程序。

```csharp
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().AddFilter("Finlight", LogLevel.Information));
using var client = new FinlightClient(new FinlightConfig { ApiKey = key, LoggerFactory = loggerFactory });
```

## 数据说明

- 旧文章的 `PublishedAt` 可能是 `"1970-01-01T00:00:00.000Z"`。按原样传递。
- 空文本以 `null` 传递（`Summary`、`Content`）。
- 在角色出现之前打标签的文章，`Role` 和 `Relevance` 为 `null`，`Events` 为 `[]`。
- 对于 ID 出现之前索引的文章，`Articles.GetAsync(id)` 返回 404。请使用 `GetByLinkAsync`。
- 旧文章的 `AvailableAt` 为 `null`，raw 流上始终为 `null`。

## 从 1.x 迁移

完整指南见 [docs.finlight.me](https://docs.finlight.me/migration/v2-to-v3)。简要版本：

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

将 Webhook 切换到 v3 的四个步骤：

1. 部署本 SDK。
2. 对 `WrongPayloadVersion` 以 2xx 应答并保留其 `Raw` JSON。
3. 在控制台中将 Webhook 切换为 `payloadVersion` 3。
4. 等待约 2 小时：切换前排队的重试仍以旧格式到达。

1.x 和 2.x 不能在同一应用中并存（程序集名称相同）。不要在同一个密钥上同时运行 1.x 和 2.x 的流：它们共享连接上限。

## 开发

```bash
dotnet test                                                  # unit and conformance tests, no network
scripts/sync-conformance.sh --from <monorepo>/shared/contracts/api-v3
scripts/sync-conformance.sh --check
python3 scripts/generate-vocabulary.py conformance/vocab.json src/Finlight.Client/Vocabulary.g.cs
python3 scripts/check-readme.py
```

`conformance/` 是契约工具包的副本，带有 `CONTRACT_VERSION` 和 sha256 `MANIFEST`；`CONFORMANCE.md` 将每个用例映射到
其测试。`examples/` 中的示例由测试构建并运行。实时测试只针对 dev，需要 `FINLIGHT_API_KEY`、`FINLIGHT_BASE_URL` 和
`FINLIGHT_WSS_URL`：`dotnet test tests/Finlight.Client.LiveTests`。它们不属于解决方案。

## 许可证

MIT
