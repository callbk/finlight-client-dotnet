# finlight .NET Client

*[English](README.md) | [简体中文](README.zh-CN.md) | 日本語 | [한국어](README.ko.md)*

[finlight.me](https://finlight.me) 金融ニュース API の公式 .NET クライアントです。
完全な API ドキュメント: [docs.finlight.me](https://docs.finlight.me)

## 主な機能

- 🔎 **記事検索** — 付加情報の付いた金融ニュースに対する全文およびフィールド検索
- ⚡ **リアルタイムストリーミング** — enhanced および raw の WebSocket フィードを `IAsyncEnumerable` として提供
- 🔁 **標準で堅牢** — バックオフ付きリトライ、自動再接続、先回りのコネクションローテーション
- 🔐 **Webhook 検証** — リプレイ攻撃対策付きの HMAC-SHA256 署名チェック
- 🪶 **軽量** — 依存関係は 1 つのみ（`Microsoft.Extensions.Logging.Abstractions`）、null 注釈付き、API ドキュメント完備

## インストール

```bash
dotnet add package Finlight.Client
```

.NET 8 以降が必要です。

## クイックスタート

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

### 記事を検索する

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

API は総件数を返しません。短いページまたは空のページが返るまで `Page` を進めてください。

### URL から単一の記事を取得する

```csharp
var article = await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams
{
    Link = "https://www.example.com/some-article",
    IncludeContent = true,
});
```

### ニュースソースを一覧取得する

```csharp
var sources = await client.Sources.GetSourcesAsync();
```

## WebSocket ストリーミング

### Enhanced ストリーム

付加情報の付いた記事（センチメント、エンティティ、本文）を配信します。直近 10 件の配信内での重複は抑制されます。

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

### Raw ストリーム

付加情報のない記事を、より低い遅延で配信します。クエリ言語は `source:`、`title:`、`summary:` の各フィールドに限定されます。

```csharp
await foreach (var article in client.RawWebSocket.StreamAsync(new GetRawArticlesWebSocketParams
{
    Query = "title:earnings",
}, cancellationToken))
{
    Console.WriteLine($"{article.Source}: {article.Title}");
}
```

### ストリーミングの挙動

- 再接続（500ms から 10s への指数バックオフ、レート制限による待機、サーバー側 2 時間上限の手前での先回りローテーション）は内部で処理されます。
- 別の接続がスロットを取得してサーバーがこのクライアントを切り離した場合、ストリームは**正常に**終了します（`Takeover` を参照）。
- サーバーが接続を恒久的に拒否した場合は `FinlightBlockedException` が送出されます。再接続しても解決しません。
- トークンをキャンセルするか、ループを `break` すると停止します。`break` の場合は接続がきれいに閉じられます。
- 1 つのクライアントインスタンスにつき同時に 1 つのストリームのみです。2 つ目を並行して開くと `InvalidOperationException` が送出されます。

### WebSocket オプションのカスタマイズ

```csharp
using Finlight.WebSockets;

var ws = new ArticleWebSocketClient(
    new FinlightClientOptions { ApiKey = "your-api-key" },
    new FinlightWebSocketOptions
    {
        Takeover = true, // 別のクライアントから接続スロットをテイクオーバーする
        OnClose = (code, reason) => Console.WriteLine($"closed: {code} {reason}"),
    });
```

| オプション | デフォルト | 説明 |
| --- | --- | --- |
| `PingInterval` | 25s | アプリケーションレベルの ping 間隔 |
| `PongTimeout` | 60s | pong が届かない場合に強制的に再接続 |
| `BaseReconnectDelay` | 500ms | 初回再接続のバックオフ |
| `MaxReconnectDelay` | 10s | バックオフの上限 |
| `ConnectionLifetime` | 115min | サーバー側 2 時間上限を下回る先回りローテーション |
| `Takeover` | `false` | 同じキーの既存コネクションをテイクオーバーする |
| `OnClose` | – | (closeCode, reason) を受け取るコールバック |

## Webhook

生の、変更されていないリクエストボディで受信 Webhook を検証します:

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
        // article を処理する ...
        return Results.Ok();
    }
    catch (FinlightWebhookVerificationException)
    {
        return Results.Unauthorized();
    }
});
```

署名は `"{timestamp}.{body}"` に対する HMAC-SHA256 です（タイムスタンプヘッダーがない場合はボディのみ）。比較は定数時間で行われ、リプレイの許容範囲は 5 分です。

## 設定

| オプション | デフォルト | 説明 |
| --- | --- | --- |
| `ApiKey` | –（必須） | あなたの finlight API キー |
| `BaseUrl` | `https://api.finlight.me` | REST のベース URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket のベース URL |
| `Timeout` | 5s | 試行ごとのタイムアウト（REST と WebSocket ハンドシェイク） |
| `RetryCount` | 3 | REST の総試行回数 |
| `TimeProvider` | システム | テスト用の時刻の差し替え |

### 依存性注入

本クライアントは `IHttpClientFactory` と併用でき、呼び出し側が所有する `HttpClient` を破棄することはありません:

```csharp
services.AddHttpClient("finlight", http => http.Timeout = Timeout.InfiniteTimeSpan);
services.AddSingleton(sp => new FinlightClient(
    new FinlightClientOptions { ApiKey = configuration["Finlight:ApiKey"]! },
    sp.GetRequiredService<IHttpClientFactory>().CreateClient("finlight"),
    sp.GetService<ILoggerFactory>()));
```

## ロギング

`ILoggerFactory` を渡すと構造化ログ（接続のライフサイクル、リトライ、プロトコルイベント）が得られます。渡さない場合、クライアントは何も出力しません。

## エラーハンドリング

| 例外 | 意味 |
| --- | --- |
| `FinlightApiException` | リトライ後も 2xx 以外の REST 応答。`StatusCode`、`ReasonPhrase`、`Body` を保持します |
| `FinlightBlockedException` | WebSocket が恒久的に拒否された（クローズコード 1008） |
| `FinlightWebhookVerificationException` | Webhook の署名、タイムスタンプ、またはペイロードの検証に失敗 |
| `TimeoutException` | 1 回の REST 試行が `Timeout` を超過 |

リトライ: ステータス 429、500、502、503、504 は指数バックオフ（500ms · 2^(試行回数−1)）で、`RetryCount` の総試行回数までリトライされます。

## テスト

```bash
dotnet test                            # ユニットテスト（オフライン）
FINLIGHT_API_KEY=... dotnet test       # 実 API に対する統合テストを追加で実行
```

## ライセンス

[MIT](LICENSE)

## サポート

- 📖 [ドキュメント](https://docs.finlight.me)
- 📧 info@finlight.me
- 🐛 [GitHub Issues](https://github.com/callbk/finlight-client-dotnet/issues)
- 🌐 [finlight.me](https://finlight.me)
- 🗾 [日本語の製品ページ](https://finlight.me/ja/news-api)
