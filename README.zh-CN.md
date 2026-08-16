# finlight .NET Client

*[English](README.md) | 简体中文 | [日本語](README.ja.md) | [한국어](README.ko.md)*

[finlight.me](https://finlight.me) 财经新闻 API 的官方 .NET 客户端。
完整 API 文档：[docs.finlight.me](https://docs.finlight.me)

## 功能特性

- 🔎 **文章检索** —— 对经过增强的财经新闻执行全文和字段查询
- ⚡ **实时流式推送** —— enhanced 和 raw 两种 WebSocket 数据流，以 `IAsyncEnumerable` 形式提供
- 🔁 **默认具备韧性** —— 带退避的重试、自动重连、主动连接轮换
- 🔐 **Webhook 验证** —— HMAC-SHA256 签名校验，含重放防护
- 🪶 **轻量** —— 仅一个依赖（`Microsoft.Extensions.Logging.Abstractions`），带可空性标注，API 文档完整

## 安装

```bash
dotnet add package Finlight.Client
```

需要 .NET 8 及以上版本。

## 快速开始

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

### 检索文章

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

API 不返回总条数；请递增 `Page`，直到返回不足一页或空页为止。

### 按 URL 获取单篇文章

```csharp
var article = await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams
{
    Link = "https://www.example.com/some-article",
    IncludeContent = true,
});
```

### 列出新闻源

```csharp
var sources = await client.Sources.GetSourcesAsync();
```

## WebSocket 流式推送

### Enhanced 流

经过增强的文章（含情感、实体、正文）。最近 10 条推送内的重复内容会被抑制。

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

### Raw 流

未经增强的文章，延迟更低。查询语言仅限 `source:`、`title:` 和 `summary:` 字段。

```csharp
await foreach (var article in client.RawWebSocket.StreamAsync(new GetRawArticlesWebSocketParams
{
    Query = "title:earnings",
}, cancellationToken))
{
    Console.WriteLine($"{article.Source}: {article.Title}");
}
```

### 流式推送语义

- 重连（指数退避，从 500ms 到 10s；速率限制等待；在服务端 2 小时连接上限之前主动轮换）均在内部处理。
- 当服务端因另一个连接占用了名额而抢占本客户端时，数据流会**正常**结束（参见 `Takeover`）。
- 当服务端永久拒绝该连接时抛出 `FinlightBlockedException` —— 此时重连无济于事。
- 取消 token 或 `break` 跳出循环即可停止；break 会干净地关闭连接。
- 每个客户端实例同时只允许一个活跃数据流；并发开启第二个会抛出 `InvalidOperationException`。

### 自定义 WebSocket 选项

```csharp
using Finlight.WebSockets;

var ws = new ArticleWebSocketClient(
    new FinlightClientOptions { ApiKey = "your-api-key" },
    new FinlightWebSocketOptions
    {
        Takeover = true, // 从另一个客户端手中接管连接名额
        OnClose = (code, reason) => Console.WriteLine($"closed: {code} {reason}"),
    });
```

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `PingInterval` | 25s | 应用层 ping 频率 |
| `PongTimeout` | 60s | 未收到 pong 时强制重连 |
| `BaseReconnectDelay` | 500ms | 首次重连退避 |
| `MaxReconnectDelay` | 10s | 退避上限 |
| `ConnectionLifetime` | 115min | 主动轮换，低于服务端 2 小时上限 |
| `Takeover` | `false` | 接管同一密钥下已有的连接 |
| `OnClose` | – | 回调，参数为 (closeCode, reason) |

## Webhook

使用原始、未经修改的请求体验证收到的 Webhook：

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
        // 处理 article ……
        return Results.Ok();
    }
    catch (FinlightWebhookVerificationException)
    {
        return Results.Unauthorized();
    }
});
```

签名是对 `"{timestamp}.{body}"` 计算的 HMAC-SHA256（若无 timestamp 请求头，则仅对 body 计算），采用常量时间比较，重放容差为 5 分钟。

## 配置

| 选项 | 默认值 | 说明 |
| --- | --- | --- |
| `ApiKey` | –（必填） | 你的 finlight API 密钥 |
| `BaseUrl` | `https://api.finlight.me` | REST 基础地址 |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket 基础地址 |
| `Timeout` | 5s | 单次尝试超时（REST 和 WebSocket 握手） |
| `RetryCount` | 3 | REST 总尝试次数 |
| `TimeProvider` | 系统时钟 | 供测试覆盖的时钟 |

### 依赖注入

该客户端可与 `IHttpClientFactory` 配合使用，且绝不会释放由调用方持有的 `HttpClient`：

```csharp
services.AddHttpClient("finlight", http => http.Timeout = Timeout.InfiniteTimeSpan);
services.AddSingleton(sp => new FinlightClient(
    new FinlightClientOptions { ApiKey = configuration["Finlight:ApiKey"]! },
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("finlight"),
    sp.GetService<ILoggerFactory>()));
```

## 日志

传入 `ILoggerFactory` 即可获得结构化日志（连接生命周期、重试、协议事件）。不传时客户端保持静默。

## 错误处理

| 异常 | 含义 |
| --- | --- |
| `FinlightApiException` | 重试后仍返回非 2xx 的 REST 响应；携带 `StatusCode`、`ReasonPhrase`、`Body` |
| `FinlightBlockedException` | WebSocket 被永久拒绝（关闭码 1008） |
| `FinlightWebhookVerificationException` | Webhook 的签名、时间戳或负载校验失败 |
| `TimeoutException` | 单次 REST 尝试超过 `Timeout` |

重试策略：状态码 429、500、502、503 和 504 会重试，总尝试次数不超过 `RetryCount`，采用指数退避（500ms · 2^(尝试次数−1)）。

## 测试

```bash
dotnet test                            # 单元测试（离线）
FINLIGHT_API_KEY=... dotnet test       # 额外运行针对线上 API 的集成测试
```

## 许可证

[MIT](LICENSE)

## 支持

- 📖 [文档](https://docs.finlight.me)
- 📧 info@finlight.me
- 🐛 [GitHub Issues](https://github.com/callbk/finlight-client-dotnet/issues)
- 🌐 [finlight.me](https://finlight.me)
- 🌏 [中文产品页](https://finlight.me/zh/news-api)
