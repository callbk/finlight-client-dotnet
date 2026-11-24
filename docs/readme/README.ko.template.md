# finlight client for .NET

*[English](README.md) | [简体中文](README.zh-CN.md) | [日本語](README.ja.md) | 한국어*

> 이 문서는 [README.md](README.md)의 번역이며 업데이트가 늦을 수 있습니다. 내용이 다르면 영어판이 우선합니다.

> **Breaking release.** 2.0.0은 finlight API v3만 지원합니다. 1.x용 코드는 그대로 컴파일되지 않습니다.
> 1.x(API v2)를 계속 사용하려면 `Version="[1.0.0,2.0.0)"`로 고정하세요. [1.x에서 마이그레이션](#1x에서-마이그레이션)을 참고하세요.

[finlight](https://finlight.me) 뉴스 API용 .NET 클라이언트입니다. REST로 금융 뉴스, 기업, 소스를 검색·조회하고,
WebSocket으로 기사를 실시간으로 받고, 웹훅을 검증합니다.

## 버전

| SDK version | API version | Status |
|---|---|---|
| 2.x | v3 (REST `/v3`, WebSocket `payloadVersion` 3, v3 웹훅) | 현재 |
| 1.x | v2 | 더 이상 릴리스 없음 |

SDK 버전과 API 버전은 서로 독립적입니다. API v3를 지원하는 finlight SDK: TypeScript 4.x, Python 3.x, PHP 2.x,
Go 모듈 `/v2`, .NET 2.x, Java 1.x, Ruby 1.x, Rust 0.2.

## 요구 사항

- .NET 8 이상 (.NET 8과 .NET 10에서 테스트)
- `Microsoft.Extensions.Logging.Abstractions` 8.0.0+ 및 `Microsoft.Extensions.Primitives` 8.0.0+ (패키지와 함께 설치됨)

## 설치

{{CODE1}}

## 빠른 시작

{{CODE2}}

I/O를 하는 메서드는 모두 비동기이며, 이름이 `Async`로 끝나고 `CancellationToken`을 받습니다. 파라미터 타입의 각 속성은
API의 키 하나에 대응합니다(`OrderBy`는 `orderBy`). 설정한 속성만 전송됩니다: `new SearchParams()`는 `{}`를 보냅니다.

## 기사 검색

{{CODE3}}

`SearchAllAsync`는 페이지를 따라갑니다. 호출할 때 요청을 고정하고 이후 페이지에는 cursor만 추가합니다. 페이지마다
쿼터를 쓰므로 `MaxPages` 페이지(기본 10) 후에 멈춥니다.

{{CODE4}}

같은 파라미터에 `Cursor = results.NextCursor`를 넣으면 이어서 가져옵니다. 결과는 한 번만 순회할 수 있습니다.
형식 오류(예: `Limit = 0`, `Languages = []`)는 전송 전에 `FinlightValidationException`을 던지며, `SearchAllAsync`와
스트림도 마찬가지입니다. 어휘 값은 로컬에서 검사하지 않으므로 서버의 새 값은 SDK 업데이트 없이 동작합니다:
`EventTypes = ["new_type"]`는 그대로 전송됩니다.

## 기사, 기업, 소스 조회

{{CODE5}}

- 찾지 못하면 `NotFoundException`을 던집니다. 빈 결과가 아닙니다.
- 기사 ID 도입 전에 색인된 기사는 링크로만 가져올 수 있습니다.
- 기업 조회에는 `entityAccess` 권한이 필요합니다(없으면 `ForbiddenException`).
- 기업과 소스 조회는 기사 요청처럼 요청마다 1크레딧이 듭니다(`GetManyAsync`는 ID마다가 아니라 요청마다 1).
  304 재검증과 오류도 1크레딧이 듭니다. 다른 호출처럼 429가 올 수 있습니다.
- SDK는 기업 레코드를 캐시하지 않습니다. `GetCacheInfo()`는 레코드의 `Etag`와 `CacheControl`을 제공합니다.
  크레딧을 아끼려면 레코드를 직접 보관하고 `max-age` 동안 다시 호출하지 마세요. ETag는
  `new CompanyGetOptions { IfNoneMatch = etag }`로 넘길 수 있습니다. 304 응답은 `null`을 반환하지만 이것도 1 크레딧이
  듭니다. `IfNoneMatch`가 없으면 SDK는 `If-None-Match`를 보내지 않습니다.
- `CompanyList`와 `SourceList`는 `Skipped` 속성이 있는 읽기 전용 리스트입니다. 검색 페이지의 `ArticlePage.Skipped`도
  같습니다. 읽을 수 없는 항목은 여기에 들어가고 경고로 로그에 남습니다.

## 없음, null, 빈 값

일부 키는 요금제나 `include`에 따라 달라집니다. 최대 세 가지 상태가 있으며 `Optional<T>`로 구분합니다.

| 상태 | 의미 | 확인 방법 |
|---|---|---|
| 없음 | 요청하지 않음, 권한 없음, 또는 raw 스트림 | `article.ContentPresent`가 `false` |
| `null` | 요청했고 권한도 있지만 값이 없음 | `ContentPresent`가 `true`이고 `article.Content.Value`가 `null` |
| 값 | 그 값 | `article.Content.Value`가 설정됨 |

- `Content`와 `Sentiment`는 세 가지 상태를 모두 가집니다. `ContentPresent`와 `SentimentPresent`를 사용하세요.
- `Companies`와 `Events`는 없거나 리스트입니다. `[]`는 태깅했지만 찾은 것이 없다는 뜻입니다. `CompaniesPresent`와
  `EventsPresent`를 사용하세요. `[]`가 "권한 없음"을 뜻하는 일은 없습니다.
- 같은 응답에 `companies`가 없으면 `EventTag.CompanyIds`는 없습니다.
- `fullArticleAccess`가 없으면 `Source.HasContent`는 없습니다. "콘텐츠 없음"이 아니라 "알 수 없음"입니다.
- 소스가 분류되지 않았으면 `article.Source.Type`과 `Source.Type`은 `null`입니다. `SourceTypes`는 그런 기사와 일치하지 않고, `ExcludeSourceTypes`는 그런 기사를 유지합니다.
- 타임스탬프는 전송된 문자열 그대로입니다(`PublishedAt`). `PublishedAtTime` 등 `...Time` 속성은 RFC 3339로 엄격하게
  파싱하며 읽을 수 없으면 `null`을 반환합니다.
- 새 서버의 알 수 없는 키는 무시됩니다. `article.GetRawJson()`은 받은 JSON을 반환합니다.
  `JsonSerializer.Serialize(article)`로 모델을 다시 쓰면 없는 키는 없는 채로 남습니다.
- 어휘는 열린 `readonly record struct`입니다. `CompanyRole.Acquirer`, `EventType.Mna` 등은 값으로 비교하고, 알 수 없는
  값은 받은 그대로 유지되며, `CompanyRoleValues.Acquirer`는 `switch`용 `const string`입니다.

## 실시간 스트림

{{CODE6}}

- `item.Kind`는 `created`, `updated`, 또는 `other`(이 SDK가 아직 모르는 이벤트)입니다.
- `client.RawStreamAsync(new RawStreamParams { ... })`는 보강 전의 기사를 전달합니다. `include`가 없고 콘텐츠,
  감성, 기업, 이벤트도 없습니다.
- `StreamParams`와 `RawStreamParams`는 검색과 같이 `SourceTypes`와 `ExcludeSourceTypes`를 받습니다
  (`ExcludeSourceTypes = [SourceType.Blog]`). 설정했을 때만 전송됩니다. 알 수 없는 값은 스트림을
  `ValidationFailedException`으로 끝냅니다.
- `StreamOptions`의 훅: `OnAdmitted(AdmittedInfo)`, 첫 연결을 제외한 새 연결마다의 `OnReconnect(ReconnectInfo)`,
  스트림을 끝내지 않는 문제의 `OnError(FinlightException)`, 그리고 `OnQuotaWait(QuotaWaitInfo)`. 훅에서 던진 예외는
  로그에 남을 뿐 스트림을 멈추지 않습니다.
- 스트림은 25초마다 ping을 보내고, 60초 동안 pong이 없으면 다시 연결하며, 115분 후 새 연결을 엽니다. 재연결할 때마다
  같은 subscribe를 보내고 중복을 버립니다.
- 백필은 하지 않습니다. `ReconnectInfo.LastSeenAt`은 마지막으로 본 `availableAt`(raw: `firstSeenAt`)이며, 아직 받은
  것이 없으면 null입니다. `if (info.LastSeenAt is { } since)`와
  `SearchAllAsync(new SearchParams { From = since, OrderBy = OrderBy.AvailableAt })`
  (raw: `OrderBy.FirstSeenAt`)로 빈 구간을 채우세요.
- 종료성 문제는 예외로 루프를 끝냅니다: 잘못된 키(`UnauthorizedException`), WebSocket 권한 없음(`ForbiddenException`),
  거부된 subscribe(`ValidationFailedException`), 다른 연결이 넘겨받았을 때의 `PreemptedException`. 그 밖의 문제는
  다시 연결하고 `OnError`로 전달됩니다.
- 월간 WebSocket 쿼터를 다 쓰면(연결이 거부되거나 이유가 `quota_exceeded`인 선점) 스트림은 끝나지 않습니다. 쿼터가
  허용할 때까지 한 시간마다 다시 시도합니다. 대기할 때마다 경고를 로그에 남기고 `OnQuotaWait`를 호출합니다.
  `FinlightConfig`에서 `QuotaReconnect = false`로 하면 대신 오류로 스트림을 끝냅니다. `QuotaReconnectInterval`로
  한 시간 간격을 바꿀 수 있습니다.
- 루프를 벗어나거나, `CancellationToken`, 또는 `DisposeAsync()`로 소켓을 1000으로 닫고 모든 타이머를 멈춥니다.
  취소는 `OperationCanceledException`으로 전달됩니다. 클라이언트 하나로 여러 스트림을 동시에 실행할 수 있습니다.
  `client.Dispose()`는 그 모두를 닫습니다.

알아야 할 제한:

- 요금제의 연결 수 제한은 enriched, raw, 이전 SDK 연결이 함께 씁니다. 제한에 도달한 상태의 새 연결은 거부되며 SDK는
  60초 후 다시 시도합니다. `StreamOptions`에서 `Takeover = true`로 하면 대신 가장 오래된 연결을 교체합니다.
- 115분마다 하는 연결 갱신은 이전 연결을 닫기 전에 새 연결을 엽니다. 두 번째 연결이 거부되거나 실패하면(예: 한도에 자리가
  없고 `Takeover`가 꺼져 있을 때), SDK는 이전 연결을 닫고 닫힐 때까지 기다린 뒤 바로 다시 연결합니다(짧은 공백, `OnError` 없음).
  그 연결마저 거부될 때만 평소의 대기가 적용됩니다.
- subscribe 직후 서버는 일치하는 최신 기사 하나를 보냅니다(재연결할 때마다도). 오래된 기사일 수 있습니다.
- 32,000바이트를 넘는 라이브 프레임은 서버가 알림 없이 버립니다. 긴 기사에 `Include = [IncludeBlock.Content]`를
  쓰면 생길 수 있습니다. `Articles.GetAsync`로 가져오세요.
- `connection.admitted`는 서버가 subscribe를 검사하기 전에 도착합니다. 수락되었다는 뜻이 아닙니다.

## 웹훅

finlight는 모든 웹훅에 서명합니다. 무엇이든 파싱하기 전에 **원본** 요청 본문으로 검증하세요.

{{CODE7}}

- `headers`에는 ASP.NET Core의 `Request.Headers`(모든 `IEnumerable<KeyValuePair<string, StringValues>>`) 또는
  `HttpHeaders`를 넘길 수 있습니다. 이름은 대소문자를 구분하지 않고 찾으며 CGI 형식 `HTTP_X_WEBHOOK_...`도 지원합니다.
  `Webhooks.ConstructEvent(rawBody, signature, timestamp, secret)`는 두 헤더 값을 직접 받습니다.
  `Webhooks.VerifySignature(...)`는 검사만 합니다. 본문은 `byte[]` 또는 받은 UTF-8 `string`입니다.
- 타임스탬프는 앞뒤로 `Webhooks.DefaultTolerance`(300초) 안이어야 합니다(`WebhookVerifyOptions.Tolerance`,
  테스트용 시계는 `WebhookVerifyOptions.Now`).
- `WebhookVerificationException.Reason`이 이유를 알려 줍니다. 예: `signature_mismatch`, `timestamp_outside_tolerance`.
- 대시보드 테스트 웹훅은 `e.IsTest`가 true입니다. 요금제로 잘리지 않습니다.
- 410으로 응답하지 마세요. 웹훅이 즉시 비활성화됩니다. 2xx가 아닌 응답이 10번 연속되어도 비활성화됩니다.

원본 본문 얻기:

| 프레임워크 | 원본 본문 |
|---|---|
| ASP.NET Core minimal API | `await request.Body.CopyToAsync(buffer)` (`[FromBody]` 모델 없이) |
| ASP.NET Core MVC | `Request.EnableBuffering()` 후 모델 바인딩 전에 `Request.Body`를 읽거나, 아무것도 바인딩하지 않음 |
| Azure Functions (isolated) | `HttpRequestData`에서 `await req.Body.CopyToAsync(buffer)` |

## 오류와 재시도

모든 예외는 `FinlightException`에서 파생되며 `Kind`를 가집니다: `api`, `validation`, `network`, `timeout`, `decode`,
`webhook_verification`, `preempted`.

| 예외 | 발생 시점 |
|---|---|
| `FinlightApiException` | API가 오류로 응답함: REST, 거부된 스트림 연결, 스트림 오류. `Status`, `Code`, `Type`, `Title`, `Detail`, `RequestId`, `Errors`(`FieldIssue`), `ApiKind`, `Raw`. 하위 클래스: `ValidationFailedException`, `CursorInvalidException`, `CursorMismatchException`, `UnauthorizedException`, `ForbiddenException`, `NotFoundException`, `RateLimitedException`, `InternalServerException`. 그 밖에는 `ApiKind`가 `other`. |
| `RateLimitedException` | `LimitType`은 `monthly`, `burst`, `unknown` 또는 더 새로운 값. `RetryAfter`는 `TimeSpan`. |
| `FinlightValidationException` | 요청이 로컬에서 거부됨. `Param`이 키를 알려 줌. |
| `FinlightNetworkException`, `FinlightTimeoutException` | 네트워크 장애 또는 시간 초과. 원래 오류는 `InnerException`. |
| `FinlightDecodeException` | 응답을 읽을 수 없음(`Path`, `Raw`). |
| `WebhookVerificationException` | 웹훅 검증 실패(`Reason`). |
| `PreemptedException` | 다른 연결이 스트림을 넘겨받음(`Reason`, `NewLeaseId`). `QuotaReconnect = false`이면 쿼터 소진 때도. |

재시도: 5xx, 네트워크 오류, 시간 초과는 대기 시간을 늘려 가며 최대 3번 재시도하며, 검색도 포함됩니다. 400, 401, 403,
404와 월간 429는 재시도하지 않습니다. 월간 429는 `SearchAllAsync` 안에서도 즉시 실패합니다. 쿼터를 기다리는 것은
스트림뿐입니다(위 참조). 버스트 429는 `Retry-After`(헤더가 없으면 10초)만큼 정확히 기다린 뒤 한 번 더 시도합니다.
기본으로 켜져 있고 `MaxRetries`에 포함되지 않으며 `RetryOnBurst = RetryOnBurst.Off`로 끌 수 있습니다. 호출마다 자체
`X-Request-Id`를 보내며 재시도 때도 같습니다. 토큰을 취소하면 대기를 포함해 즉시 멈추고 `OperationCanceledException`으로
그대로 전달됩니다.

## 설정

{{CODE8}}

| 키 | 기본값 | 의미 |
|---|---|---|
| `ApiKey` | 필수 | API 키, 비어 있으면 안 됨 |
| `BaseUrl` | `https://api.finlight.me` | REST 기본 URL |
| `WssUrl` | `wss://wss.finlight.me` | WebSocket URL, raw는 `/raw` |
| `Timeout` | 30초 | REST 시도마다 |
| `TotalTimeout` | 없음 | 호출마다의 상한, 재시도와 버스트 대기 포함 |
| `MaxRetries` | 3 | 5xx, 네트워크 오류, 시간 초과의 재시도 횟수 |
| `RetryBaseDelay`, `RetryMaxDelay` | 0.5초, 8초 | 재시도 사이에 늘어나는 대기 |
| `RetryOnBurst` | `Once` | `Off`는 버스트 재시도를 끔 |
| `BurstWait` | 10초 | `Retry-After`가 없을 때의 버스트 대기 |
| `MaxPages` | 10 | `SearchAllAsync`의 페이지 상한 |
| `MaxResponseBytes` | 32 MiB | REST 응답의 크기 상한 |
| `PingInterval` | 25초 | 스트림 ping |
| `WatchdogTimeout` | 60초 | 이 시간 동안 pong이 없으면 재연결 |
| `RotationInterval` | 115분 | 2시간 제한 전에 새 연결 |
| `ReconnectBaseDelay`, `ReconnectMaxDelay` | 1초, 30초 | 재연결 사이에 늘어나는 대기 |
| `LongBackoff` | 60초 | 연결 제한이나 429로 거부된 뒤의 대기 |
| `QuotaReconnect` | `true` | 끝내지 않고 월간 WebSocket 쿼터를 기다림 |
| `QuotaReconnectInterval` | 1시간 | 쿼터 시도 사이의 대기 |
| `SearchFailedThreshold` | 5 | `OnError`로 넘기기 전 연속 `SEARCH_FAILED` 횟수 |
| `DedupeCacheSize` | 1000 | 스트림마다 기억하는 전달 수 |
| `MaxMessageBytes` | 32 MiB | 스트림 메시지의 크기 상한(최소 16 MiB) |
| `ConnectTimeout` | 30초 | WebSocket 핸드셰이크 |
| `LoggerFactory` | 없음(출력 없음) | 직접 만든 `ILoggerFactory` |
| `TimeProvider` | `TimeProvider.System` | 재시도 대기와 스트림 타이머의 시계(테스트용) |
| `HttpClient` | 없음 | REST와 WebSocket 핸드셰이크용 직접 만든 `HttpClient` |

직접 만든 `HttpClient`를 쓰면 그 시간 제한, 프록시, 핸들러가 적용됩니다(SDK의 `Timeout`은 적용되지 않음). 복원력
핸들러나 Polly 재시도를 추가하지 말고 짧은 `HttpClient.Timeout`도 설정하지 마세요. SDK가 직접 재시도하며 시도마다
쿼터를 씁니다. 리디렉션은 끄세요(핸들러에서 `AllowAutoRedirect = false`). API 키 헤더가 다른 호스트로 가기 때문에 SDK 자체
핸들러는 리디렉션을 따르지 않습니다. SDK는 자신이 만들지 않은 클라이언트를 해제하지 않습니다. `IHttpClientFactory`로 HTTP 트래픽을 로그에
남긴다면 `RedactLoggedHeaders(["X-API-KEY"])`로 키를 숨기세요.

## 로깅

`LoggerFactory`를 넘기면 SDK는 카테고리 `Finlight`로 `ILogger`를 통해 로그를 남깁니다. 넘기지 않으면 아무것도
출력하지 않습니다(`NullLogger`). 경고는 건너뛴 항목, 재연결, 쿼터 대기를 다룹니다. API 키, 웹훅 시크릿, 서명, 전체
본문은 로그, 예외 메시지, `ToString()`에 나타나지 않습니다. SDK는 콘솔에 쓰지 않고, 전역 로그 설정을 바꾸지 않으며,
전역 핸들러도 설치하지 않습니다.

{{CODE9}}

## 데이터 참고 사항

- 오래된 기사의 `PublishedAt`은 `"1970-01-01T00:00:00.000Z"`일 수 있습니다. 받은 그대로 전달됩니다.
- 빈 텍스트는 `null`로 옵니다(`Summary`, `Content`).
- 역할 도입 전에 태깅된 기사는 `Role`과 `Relevance`가 `null`, `Events`가 `[]`입니다.
- ID 도입 전에 색인된 기사에 대해 `Articles.GetAsync(id)`는 404를 반환합니다. `GetByLinkAsync`를 쓰세요.
- 오래된 기사의 `AvailableAt`은 `null`이며 raw 스트림에서는 항상 `null`입니다.

## 1.x에서 마이그레이션

전체 가이드는 [docs.finlight.me](https://docs.finlight.me/migration/v2-to-v3)에 있습니다. 요약:

{{MIGRATION_TABLE}}

웹훅을 v3로 전환하는 네 단계:

1. 이 SDK를 배포합니다.
2. `WrongPayloadVersion`에 2xx로 응답하고 그 `Raw` JSON을 보관합니다.
3. 대시보드에서 웹훅을 `payloadVersion` 3으로 전환합니다.
4. 약 2시간 기다립니다. 전환 전에 대기열에 들어간 재시도는 이전 형식으로 도착합니다.

1.x와 2.x는 한 애플리케이션에 함께 로드할 수 없습니다(어셈블리 이름이 같음). 같은 키로 1.x와 2.x 스트림을 동시에
실행하지 마세요. 연결 수 제한을 함께 씁니다.

## 개발

{{CODE10}}

`conformance/`는 계약 키트의 사본으로 `CONTRACT_VERSION`과 sha256 `MANIFEST`를 포함합니다. `CONFORMANCE.md`는 키트의
각 사례를 해당 테스트에 연결합니다. `examples/`의 예제는 테스트가 빌드하고 실행합니다. 라이브 테스트는 dev에만 실행하며
`FINLIGHT_API_KEY`, `FINLIGHT_BASE_URL`, `FINLIGHT_WSS_URL`이 필요합니다: `dotnet test tests/Finlight.Client.LiveTests`.
솔루션에는 포함되지 않습니다.

## 라이선스

MIT
