# finlight client for .NET

*[English](README.md) | [简体中文](README.zh-CN.md) | 日本語 | [한국어](README.ko.md)*

> この文書は [README.md](README.md) の翻訳で、更新が遅れることがあります。内容が異なる場合は英語版が優先されます。

> **Breaking release.** 2.0.0 は finlight API v3 のみに対応します。1.x 向けのコードはそのままではコンパイルできません。
> 1.x（API v2）を使い続ける場合は `Version="[1.0.0,2.0.0)"` に固定してください。[1.x からの移行](#1x-からの移行) を参照してください。

[finlight](https://finlight.me) ニュース API の .NET クライアントです。REST で金融ニュース、企業、ソースを検索・参照し、
WebSocket で記事をリアルタイムに受信し、Webhook を検証できます。

## バージョン

| SDK version | API version | Status |
|---|---|---|
| 2.x | v3（REST `/v3`、WebSocket `payloadVersion` 3、v3 Webhook） | 現行 |
| 1.x | v2 | 今後のリリースなし |

SDK のバージョンと API のバージョンは独立しています。API v3 に対応する finlight SDK：TypeScript 4.x、Python 3.x、
PHP 2.x、Go モジュール `/v2`、.NET 2.x、Java 1.x、Ruby 1.x、Rust 0.2。

## 動作要件

- .NET 8 以降（.NET 8 と .NET 10 でテスト済み）
- `Microsoft.Extensions.Logging.Abstractions` 8.0.0 以上と `Microsoft.Extensions.Primitives` 8.0.0 以上（パッケージと一緒に入ります）

## インストール

```bash
dotnet add package Finlight.Client --version "[2.0.0,3.0.0)"
```

## クイックスタート

```csharp
using Finlight;

using var client = new FinlightClient("YOUR_API_KEY");
var page = await client.Articles.SearchAsync(new SearchParams { Query = "ticker:NVDA", Limit = 10 });
foreach (var article in page.Articles)
{
    Console.WriteLine(article.Title);
}
```

I/O を伴うメソッドはすべて非同期で、名前は `Async` で終わり、`CancellationToken` を受け取ります。パラメーター型の各プロパティは
API のキー 1 つに対応します（`OrderBy` は `orderBy`）。設定したプロパティだけが送信されます：`new SearchParams()` は `{}` を送ります。

## 記事の検索

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

`SearchAllAsync` はページをたどります。呼び出した時点でリクエストを固定し、後続のページでは cursor だけを追加します。
ページごとにクォータを消費するため、`MaxPages` ページ（既定 10）で止まります。

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

同じパラメーターに `Cursor = results.NextCursor` を付けると続きを取得できます。結果は 1 回だけ反復できます。
形の誤り（`Limit = 0` や `Languages = []` など）は送信前に `FinlightValidationException` になります。`SearchAllAsync`
とストリームでも同じです。語彙の値はローカルでは検査しないので、サーバーの新しい値は SDK の更新なしで使えます：
`EventTypes = ["new_type"]` はそのまま送信されます。

## 記事・企業・ソースの参照

```csharp
var article = await client.Articles.GetAsync("EU8JwpsZ8iJehuyPZjMqRz", new LookupOptions { Include = [IncludeBlock.Content] });
article = await client.Articles.GetByLinkAsync("https://www.reuters.com/...");

var companies = await client.Companies.SearchAsync(new CompanySearchParams { Q = "NVDA", Mic = "XNAS" }); // at most 10, no pages
companies = await client.Companies.GetManyAsync([4821, 1001]);
var company = await client.Companies.GetAsync(4821);   // revalidated with its ETag next time
Console.WriteLine(company?.GetCacheInfo()?.Etag);

var sources = await client.Sources.ListAsync();
```

- 見つからない場合は `NotFoundException` になります。空の結果にはなりません。
- 記事 ID の導入前にインデックスされた記事はリンクでのみ取得できます。
- 企業の参照には `entityAccess` の権限が必要です（ない場合は `ForbiddenException`）。
- 企業とソースの参照は、記事のリクエストと同じくリクエストごとに 1 クレジットかかります（`GetManyAsync` は ID ごとではなく
  リクエストごとに 1）。304 の再検証とエラーも 1 クレジットかかります。他の呼び出しと同様に 429 になることがあります。
- `Companies.GetAsync` は直近のレコードを保持し、ETag で再検証します（`CompanyCacheSize`）。独自の
  `new CompanyGetOptions { IfNoneMatch = etag }` を渡した場合、304 の応答は `null` を返します。`GetCacheInfo()` は
  `Etag`、`CacheControl`、`NotModified` を返します。
- `CompanyList` と `SourceList` は `Skipped` プロパティを持つ読み取り専用リストです。検索ページの `ArticlePage.Skipped`
  も同様です。読めなかったエントリはそこに入り、警告としてログに出ます。

## 欠落、null、空

一部のキーはプランや `include` によって変わります。最大 3 つの状態があり、`Optional<T>` で区別します。

| 状態 | 意味 | 確認方法 |
|---|---|---|
| 欠落 | 要求していない、権限がない、または raw ストリーム | `article.ContentPresent` が `false` |
| `null` | 要求し権限もあるが値がない | `ContentPresent` が `true` で `article.Content.Value` が `null` |
| 値 | 値そのもの | `article.Content.Value` が設定されている |

- `Content` と `Sentiment` は 3 つの状態すべてを持ちます。`ContentPresent` と `SentimentPresent` を使ってください。
- `Companies` と `Events` は欠落かリストです。`[]` はタグ付け済みで何も見つからなかったことを意味します。
  `CompaniesPresent` と `EventsPresent` を使ってください。`[]` が「権限なし」を意味することはありません。
- 同じ応答に `companies` がない限り、`EventTag.CompanyIds` は欠落します。
- `fullArticleAccess` がない場合、`Source.HasContent` は欠落します。「コンテンツなし」ではなく「不明」です。
- ソースが未分類の場合、`article.Source.Type` と `Source.Type` は `null` です。`SourceTypes` はそのような記事に一致せず、`ExcludeSourceTypes` はそれらを残します。
- タイムスタンプは送信された文字列のままです（`PublishedAt`）。`PublishedAtTime` などの `...Time` プロパティは
  RFC 3339 で厳密に解析し、読めない値には `null` を返します。
- 新しいサーバーの未知のキーは無視されます。`article.GetRawJson()` は受信した JSON を返します。
  `JsonSerializer.Serialize(article)` でモデルを書き戻すと、欠落したキーは欠落のままです。
- 語彙はオープンな `readonly record struct` です。`CompanyRole.Acquirer`、`EventType.Mna` などは値で比較し、
  未知の値は送信されたまま保持されます。`CompanyRoleValues.Acquirer` は `switch` 用の `const string` です。

## リアルタイムストリーム

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

- `item.Kind` は `created`、`updated`、または `other`（この SDK がまだ知らないイベント）です。
- `client.RawStreamAsync(new RawStreamParams { ... })` はエンリッチ前の記事を届けます。`include` はなく、
  コンテンツ、センチメント、企業、イベントもありません。
- `StreamParams` と `RawStreamParams` は検索と同じく `SourceTypes` と `ExcludeSourceTypes` を受け付けます
  （`ExcludeSourceTypes = [SourceType.Blog]`）。設定したときだけ送信されます。未知の値はストリームを
  `ValidationFailedException` で終わらせます。
- `StreamOptions` のフック：`OnAdmitted(AdmittedInfo)`、最初以外の新しい接続ごとの `OnReconnect(ReconnectInfo)`、
  ストリームを終わらせない問題の `OnError(FinlightException)`、そして `OnQuotaWait(QuotaWaitInfo)`。
  フック内の例外はログに出るだけで、ストリームを止めません。
- ストリームは 25 秒ごとに ping を送り、60 秒 pong がなければ再接続し、115 分後に新しい接続を開きます。
  再接続のたびに同じ subscribe を送り、重複を捨てます。
- バックフィルはしません。`ReconnectInfo.LastSeenAt` は最後に見た `availableAt`（raw では `firstSeenAt`）で、まだ何も
  届いていないときは null です。`if (info.LastSeenAt is { } since)` と
  `SearchAllAsync(new SearchParams { From = since, OrderBy = OrderBy.AvailableAt })`
  （raw では `OrderBy.FirstSeenAt`）で欠けた分を取得してください。
- 終了となる問題は例外でループを終えます：無効なキー（`UnauthorizedException`）、WebSocket の権限なし（`ForbiddenException`）、
  拒否された subscribe（`ValidationFailedException`）、別の接続に引き継がれたときの `PreemptedException`。
  それ以外の問題は再接続し、`OnError` に渡されます。
- 月間 WebSocket クォータを使い切った場合（接続が拒否される、または理由 `quota_exceeded` のプリエンプト）、ストリームは
  終了しません。クォータが許すまで 1 時間ごとに再試行します。待機のたびに警告をログに出し、`OnQuotaWait` を呼びます。
  `FinlightConfig` で `QuotaReconnect = false` にすると、代わりにエラーでストリームを終えます。`QuotaReconnectInterval` で
  1 時間の間隔を変えられます。
- ループを抜ける、`CancellationToken`、または `DisposeAsync()` でソケットを 1000 で閉じ、すべてのタイマーを止めます。
  キャンセルは `OperationCanceledException` として届きます。1 つのクライアントで複数のストリームを同時に実行できます。
  `client.Dispose()` はそのすべてを閉じます。

知っておくべき制限：

- プランの接続数上限は enriched、raw、旧 SDK の接続で共有されます。上限に達しているときの新しい接続は拒否され、
  SDK は 60 秒後に再試行します。`StreamOptions` で `Takeover = true` にすると、代わりに最も古い接続を置き換えます。
- 115 分ごとの接続更新では、古い接続を閉じる前に新しい接続を開きます。2 本目の接続が拒否されたり失敗したりしたとき
  （たとえば上限に余地がなく `Takeover` がオフのとき）、SDK は古い接続を閉じ、閉じ終わるのを待ってすぐに接続し直します
  （短い空白、`OnError` は呼ばれません）。その接続も拒否されたときだけ、通常の待機になります。
- subscribe の直後、サーバーは一致する最新の記事を 1 件送ります（再接続のたびにも）。古い記事のこともあります。
- 32,000 バイトを超えるライブフレームはサーバーが通知なしに破棄します。長い記事で `Include = [IncludeBlock.Content]`
  を使うと起こりえます。`Articles.GetAsync` で取得してください。
- `connection.admitted` はサーバーが subscribe を検査する前に届きます。受け入れられたことを意味しません。

## Webhook

finlight はすべての Webhook に署名します。何かを解析する前に、**生の**リクエストボディで検証してください。

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

- `headers` には ASP.NET Core の `Request.Headers`（任意の `IEnumerable<KeyValuePair<string, StringValues>>`）または
  `HttpHeaders` を渡せます。名前は大文字小文字を区別せずに照合し、CGI 形式 `HTTP_X_WEBHOOK_...` にも対応します。
  `Webhooks.ConstructEvent(rawBody, signature, timestamp, secret)` は 2 つのヘッダー値を直接受け取ります。
  `Webhooks.VerifySignature(...)` は検査だけを行います。ボディは `byte[]` または受信した UTF-8 の `string` です。
- タイムスタンプは前後とも `Webhooks.DefaultTolerance`（300 秒）以内でなければなりません
  （`WebhookVerifyOptions.Tolerance`、テスト用の時計は `WebhookVerifyOptions.Now`）。
- `WebhookVerificationException.Reason` が理由を示します。例：`signature_mismatch`、`timestamp_outside_tolerance`。
- ダッシュボードのテスト Webhook では `e.IsTest` が true です。プランによる制限を受けません。
- 410 で応答しないでください。Webhook がすぐに無効になります。2xx 以外の応答が 10 回続いても無効になります。

生のボディの取得方法：

| フレームワーク | 生のボディ |
|---|---|
| ASP.NET Core minimal API | `await request.Body.CopyToAsync(buffer)`（`[FromBody]` モデルは使わない） |
| ASP.NET Core MVC | `Request.EnableBuffering()` の後、モデルバインドの前に `Request.Body` を読む、または何もバインドしない |
| Azure Functions（isolated） | `HttpRequestData` で `await req.Body.CopyToAsync(buffer)` |

## エラーと再試行

すべての例外は `FinlightException` から派生し、`Kind` を持ちます：`api`、`validation`、`network`、`timeout`、
`decode`、`webhook_verification`、`preempted`。

| 例外 | 発生する場面 |
|---|---|
| `FinlightApiException` | API がエラーを返した：REST、拒否されたストリーム接続、ストリームのエラー。`Status`、`Code`、`Type`、`Title`、`Detail`、`RequestId`、`Errors`（`FieldIssue`）、`ApiKind`、`Raw`。サブクラス：`ValidationFailedException`、`CursorInvalidException`、`CursorMismatchException`、`UnauthorizedException`、`ForbiddenException`、`NotFoundException`、`RateLimitedException`、`InternalServerException`。それ以外は `ApiKind` が `other`。 |
| `RateLimitedException` | `LimitType` は `monthly`、`burst`、`unknown` または新しい値。`RetryAfter` は `TimeSpan`。 |
| `FinlightValidationException` | リクエストがローカルで拒否された。`Param` がキーを示す。 |
| `FinlightNetworkException`、`FinlightTimeoutException` | ネットワーク障害またはタイムアウト。元のエラーは `InnerException`。 |
| `FinlightDecodeException` | 応答を読めなかった（`Path`、`Raw`）。 |
| `WebhookVerificationException` | Webhook の検証に失敗した（`Reason`）。 |
| `PreemptedException` | 別の接続がストリームを引き継いだ（`Reason`、`NewLeaseId`）。`QuotaReconnect = false` のときはクォータ切れでも。 |

再試行：5xx、ネットワークエラー、タイムアウトは待ち時間を延ばしながら最大 3 回再試行します。検索も含みます。
400、401、403、404、月間の 429 は再試行しません。月間の 429 は `SearchAllAsync` の中でもすぐに失敗します。
クォータを待つのはストリームだけです（上記参照）。バーストの 429 は `Retry-After`（ヘッダーがなければ 10 秒）ちょうど待って
1 回だけ再試行します。既定で有効で、`MaxRetries` には数えず、`RetryOnBurst = RetryOnBurst.Off` で無効にできます。
呼び出しごとに独自の `X-Request-Id` を送り、再試行でも同じ値です。トークンをキャンセルすると待機中も含めてすぐに止まり、
`OperationCanceledException` としてそのまま届きます。

## 設定

```csharp
using var client = new FinlightClient(new FinlightConfig
{
    ApiKey = "YOUR_API_KEY",
    BaseUrl = "https://api.finlight.me",   // a trailing slash is fine
    WssUrl = "wss://wss.finlight.me",      // raw streams use <WssUrl>/raw
    Timeout = TimeSpan.FromSeconds(30),    // per attempt
});
```

| キー | 既定値 | 意味 |
|---|---|---|
| `ApiKey` | 必須 | API キー、空は不可 |
| `BaseUrl` | `https://api.finlight.me` | REST のベース URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket の URL、raw は `/raw` |
| `Timeout` | 30 秒 | REST の試行ごと |
| `TotalTimeout` | なし | 呼び出しごとの上限、再試行とバースト待機を含む |
| `MaxRetries` | 3 | 5xx、ネットワークエラー、タイムアウトの再試行回数 |
| `RetryBaseDelay`、`RetryMaxDelay` | 0.5 秒、8 秒 | 再試行の間の増えていく待ち時間 |
| `RetryOnBurst` | `Once` | `Off` でバースト再試行を無効化 |
| `BurstWait` | 10 秒 | `Retry-After` がないときのバースト待機 |
| `MaxPages` | 10 | `SearchAllAsync` のページ上限 |
| `MaxResponseBytes` | 32 MiB | REST 応答のサイズ上限 |
| `CompanyCacheSize` | 256 | `Companies.GetAsync` が保持するレコード数、0 で無効 |
| `PingInterval` | 25 秒 | ストリームの ping |
| `WatchdogTimeout` | 60 秒 | この間 pong がなければ再接続 |
| `RotationInterval` | 115 分 | 2 時間の上限前に新しい接続 |
| `ReconnectBaseDelay`、`ReconnectMaxDelay` | 1 秒、30 秒 | 再接続の間の増えていく待ち時間 |
| `LongBackoff` | 60 秒 | 接続上限や 429 で拒否された後の待機 |
| `QuotaReconnect` | `true` | 終了せずに月間 WebSocket クォータを待つ |
| `QuotaReconnectInterval` | 1 時間 | クォータの試行間隔 |
| `SearchFailedThreshold` | 5 | `OnError` に渡すまでの連続 `SEARCH_FAILED` 回数 |
| `DedupeCacheSize` | 1000 | ストリームごとに覚えておく配信数 |
| `MaxMessageBytes` | 32 MiB | ストリームメッセージのサイズ上限（最低 16 MiB） |
| `ConnectTimeout` | 30 秒 | WebSocket ハンドシェイク |
| `LoggerFactory` | なし（無出力） | 独自の `ILoggerFactory` |
| `TimeProvider` | `TimeProvider.System` | 再試行の待機とストリームのタイマーの時計（テスト用） |
| `HttpClient` | なし | REST と WebSocket ハンドシェイク用の独自 `HttpClient` |

独自の `HttpClient` では、そのタイムアウト、プロキシ、ハンドラーが適用されます（SDK の `Timeout` は適用されません）。
レジリエンスハンドラーや Polly の再試行を追加せず、短い `HttpClient.Timeout` も設定しないでください。SDK 自身が再試行し、
試行ごとにクォータを消費します。リダイレクトはオフにしてください（ハンドラーで `AllowAutoRedirect = false`）。API キーの
ヘッダーが別のホストに送られるため、SDK 自身のハンドラーはリダイレクトに従いません。SDK は自分が作っていないクライアントを
破棄しません。`IHttpClientFactory` で HTTP 通信を
ログに出す場合は、`RedactLoggedHeaders(["X-API-KEY"])` でキーを隠してください。

## ログ

`LoggerFactory` を渡すと、SDK はカテゴリ `Finlight` で `ILogger` を通じてログを出します。渡さなければ何も出しません
（`NullLogger`）。警告はスキップしたエントリ、再接続、クォータ待機を扱います。API キー、Webhook シークレット、署名、
ボディ全体がログ、例外メッセージ、`ToString()` に出ることはありません。SDK はコンソールに書かず、グローバルなログ設定を
変えず、グローバルなハンドラーも登録しません。

```csharp
using var loggerFactory = LoggerFactory.Create(b => b.AddConsole().AddFilter("Finlight", LogLevel.Information));
using var client = new FinlightClient(new FinlightConfig { ApiKey = key, LoggerFactory = loggerFactory });
```

## データに関する注意

- 古い記事の `PublishedAt` は `"1970-01-01T00:00:00.000Z"` のことがあります。送信されたまま届きます。
- 空のテキストは `null` で届きます（`Summary`、`Content`）。
- ロールの導入前にタグ付けされた記事では `Role` と `Relevance` が `null`、`Events` が `[]` です。
- ID の導入前にインデックスされた記事には、`Articles.GetAsync(id)` が 404 を返します。`GetByLinkAsync` を使ってください。
- 古い記事の `AvailableAt` は `null` で、raw ストリームでは常に `null` です。

## 1.x からの移行

完全なガイドは [docs.finlight.me](https://docs.finlight.me/migration/v2-to-v3) にあります。概要：

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

Webhook を v3 に切り替える 4 つの手順：

1. この SDK をデプロイします。
2. `WrongPayloadVersion` に 2xx で応答し、その `Raw` JSON を保持します。
3. ダッシュボードで Webhook を `payloadVersion` 3 に切り替えます。
4. 約 2 時間待ちます。切り替え前にキューに入った再試行は古い形式で届きます。

1.x と 2.x は 1 つのアプリケーションに同時に読み込めません（アセンブリ名が同じ）。同じキーで 1.x と 2.x のストリームを
同時に実行しないでください。接続数上限を共有します。

## 開発

```bash
dotnet test                                                  # unit and conformance tests, no network
scripts/sync-conformance.sh --from <monorepo>/shared/contracts/api-v3
scripts/sync-conformance.sh --check
python3 scripts/generate-vocabulary.py conformance/vocab.json src/Finlight.Client/Vocabulary.g.cs
python3 scripts/check-readme.py
```

`conformance/` は契約キットのコピーで、`CONTRACT_VERSION` と sha256 の `MANIFEST` を含みます。`CONFORMANCE.md` は
キットの各ケースをテストに対応付けます。`examples/` のサンプルはテストでビルドされ実行されます。ライブテストは dev に
対してのみ実行し、`FINLIGHT_API_KEY`、`FINLIGHT_BASE_URL`、`FINLIGHT_WSS_URL` が必要です：
`dotnet test tests/Finlight.Client.LiveTests`。ソリューションには含まれません。

## ライセンス

MIT
