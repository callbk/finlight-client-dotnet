# finlight .NET Client

*[English](README.md) | [简体中文](README.zh-CN.md) | [日本語](README.ja.md) | 한국어*

[finlight.me](https://finlight.me) 금융 뉴스 API의 공식 .NET 클라이언트입니다.
전체 API 문서: [docs.finlight.me](https://docs.finlight.me)

## 주요 기능

- 🔎 **기사 검색** — 보강된 금융 뉴스에 대한 전문 및 필드 검색
- ⚡ **실시간 스트리밍** — enhanced 및 raw WebSocket 피드를 `IAsyncEnumerable`로 제공
- 🔁 **기본적으로 견고함** — 백오프를 적용한 재시도, 자동 재연결, 선제적 연결 교체
- 🔐 **Webhook 검증** — 재생 공격 방지를 포함한 HMAC-SHA256 서명 확인
- 🪶 **경량** — 의존성 하나뿐(`Microsoft.Extensions.Logging.Abstractions`), 널 허용성 주석 적용, API 문서 완비

## 설치

```bash
dotnet add package Finlight.Client
```

.NET 8 이상이 필요합니다.

## 빠른 시작

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

### 기사 검색

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

API는 총 개수를 반환하지 않습니다. 짧은 페이지나 빈 페이지가 반환될 때까지 `Page`를 증가시키세요.

### URL로 단일 기사 조회

```csharp
var article = await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams
{
    Link = "https://www.example.com/some-article",
    IncludeContent = true,
});
```

### 뉴스 소스 목록 조회

```csharp
var sources = await client.Sources.GetSourcesAsync();
```

## WebSocket 스트리밍

### Enhanced 스트림

보강된 기사(감성, 엔티티, 본문)를 전달합니다. 최근 10건의 전송 내 중복은 억제됩니다.

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

### Raw 스트림

보강되지 않은 기사를 더 낮은 지연으로 전달합니다. 쿼리 언어는 `source:`, `title:`, `summary:` 필드로 제한됩니다.

```csharp
await foreach (var article in client.RawWebSocket.StreamAsync(new GetRawArticlesWebSocketParams
{
    Query = "title:earnings",
}, cancellationToken))
{
    Console.WriteLine($"{article.Source}: {article.Title}");
}
```

### 스트리밍 동작

- 재연결(500ms에서 10s로의 지수 백오프, 속도 제한 대기, 서버의 2시간 연결 상한 이전의 선제적 교체)은 내부에서 처리됩니다.
- 다른 연결이 슬롯을 차지해 서버가 이 클라이언트를 선점 해제하면 스트림은 **정상적으로** 종료됩니다(`Takeover` 참조).
- 서버가 연결을 영구적으로 거부하면 `FinlightBlockedException`이 발생합니다. 재연결해도 해결되지 않습니다.
- 토큰을 취소하거나 루프를 `break`하면 중단됩니다. `break`의 경우 연결이 깔끔하게 닫힙니다.
- 클라이언트 인스턴스당 활성 스트림은 하나뿐이며, 두 번째 스트림을 동시에 열면 `InvalidOperationException`이 발생합니다.

### WebSocket 옵션 사용자 지정

```csharp
using Finlight.WebSockets;

var ws = new ArticleWebSocketClient(
    new FinlightClientOptions { ApiKey = "your-api-key" },
    new FinlightWebSocketOptions
    {
        Takeover = true, // 다른 클라이언트로부터 연결 슬롯을 테이크오버
        OnClose = (code, reason) => Console.WriteLine($"closed: {code} {reason}"),
    });
```

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `PingInterval` | 25s | 애플리케이션 수준 ping 주기 |
| `PongTimeout` | 60s | pong이 오지 않으면 강제 재연결 |
| `BaseReconnectDelay` | 500ms | 최초 재연결 백오프 |
| `MaxReconnectDelay` | 10s | 백오프 상한 |
| `ConnectionLifetime` | 115min | 서버의 2시간 상한 아래에서 선제적 교체 |
| `Takeover` | `false` | 동일한 키의 기존 연결을 테이크오버 |
| `OnClose` | – | (closeCode, reason)을 받는 콜백 |

## Webhook

원본 그대로의, 수정되지 않은 요청 본문으로 수신 Webhook을 검증합니다:

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
        // article 처리 ...
        return Results.Ok();
    }
    catch (FinlightWebhookVerificationException)
    {
        return Results.Unauthorized();
    }
});
```

서명은 `"{timestamp}.{body}"`에 대한 HMAC-SHA256입니다(타임스탬프 헤더가 없으면 본문만 사용). 비교는 상수 시간으로 이루어지며 재생 허용 범위는 5분입니다.

## 설정

| 옵션 | 기본값 | 설명 |
| --- | --- | --- |
| `ApiKey` | –(필수) | 사용자의 finlight API 키 |
| `BaseUrl` | `https://api.finlight.me` | REST 기본 URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket 기본 URL |
| `Timeout` | 5s | 시도당 타임아웃(REST 및 WebSocket 핸드셰이크) |
| `RetryCount` | 3 | REST 총 시도 횟수 |
| `TimeProvider` | 시스템 | 테스트용 시계 재정의 |

### 의존성 주입

이 클라이언트는 `IHttpClientFactory`와 함께 사용할 수 있으며, 호출자가 소유한 `HttpClient`를 절대 해제하지 않습니다:

```csharp
services.AddHttpClient("finlight", http => http.Timeout = Timeout.InfiniteTimeSpan);
services.AddSingleton(sp => new FinlightClient(
    new FinlightClientOptions { ApiKey = configuration["Finlight:ApiKey"]! },
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("finlight"),
    sp.GetService<ILoggerFactory>()));
```

## 로깅

`ILoggerFactory`를 전달하면 구조화된 로그(연결 수명 주기, 재시도, 프로토콜 이벤트)를 얻을 수 있습니다. 전달하지 않으면 클라이언트는 아무것도 출력하지 않습니다.

## 오류 처리

| 예외 | 의미 |
| --- | --- |
| `FinlightApiException` | 재시도 후에도 2xx가 아닌 REST 응답. `StatusCode`, `ReasonPhrase`, `Body`를 포함합니다 |
| `FinlightBlockedException` | WebSocket이 영구적으로 거부됨(클로즈 코드 1008) |
| `FinlightWebhookVerificationException` | Webhook의 서명, 타임스탬프 또는 페이로드 검증 실패 |
| `TimeoutException` | 단일 REST 시도가 `Timeout`을 초과 |

재시도: 상태 코드 429, 500, 502, 503, 504는 지수 백오프(500ms · 2^(시도−1))로 `RetryCount` 총 시도 횟수까지 재시도됩니다.

## 테스트

```bash
dotnet test                            # 단위 테스트(오프라인)
FINLIGHT_API_KEY=... dotnet test       # 실제 API 대상 통합 테스트 추가 실행
```

## 라이선스

[MIT](LICENSE)

## 지원

- 📖 [문서](https://docs.finlight.me)
- 📧 info@finlight.me
- 🐛 [GitHub Issues](https://github.com/callbk/finlight-client-dotnet/issues)
- 🌐 [finlight.me](https://finlight.me)
- 🇰🇷 [한국어 제품 페이지](https://finlight.me/ko/news-api)
