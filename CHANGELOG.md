# Changelog

## 2.0.0

> **Breaking release.** 2.0.0 speaks only the finlight API v3 (REST `/v3`, WebSocket `payloadVersion` 3,
> v3 webhooks). Code written for 1.x does not compile unchanged. To stay on 1.x (API v2), pin
> `Version="[1.0.0,2.0.0)"`. 1.x gets no further releases. Migration guide:
> [docs.finlight.me](https://docs.finlight.me/migration/v2-to-v3) and the "Migrating from 1.x" section of the README.

Added:

- `FinlightClient` with `FinlightConfig` (all settings with defaults, `TimeProvider`, `ILoggerFactory`,
  your own `HttpClient` for REST and the WebSocket handshake).
- REST v3: `Articles.SearchAsync`, `Articles.SearchAllAsync` (frozen body, cursor walk, `MaxPages`),
  `Articles.GetAsync`, `Articles.GetByLinkAsync`, `Companies.SearchAsync`, `Companies.GetManyAsync`,
  `Companies.GetAsync` (your own ETag as `IfNoneMatch`, 304 returns `null`; `GetCacheInfo()` gives `Etag`
  and `CacheControl`), `Sources.ListAsync`. The company and source lookups cost 1 credit per request, like an
  article request; a 304 revalidation and errors cost 1 credit too. The SDK does not cache company records:
  to save credits, keep them yourself and do not call again within `max-age`.
- `SourceTypes` and `ExcludeSourceTypes` on `SearchParams` (search and `SearchAllAsync`), `StreamParams`
  and `RawStreamParams`, with the open vocabulary `SourceType` (`newswire`, `press_release`,
  `financial_media`, `general_media`, `aggregator`, `blog`, `government`). The stream frames carry them
  only when set; an unknown value ends the stream with `ValidationFailedException`, whose `Errors` carry
  `AllowedValues` like a REST validation error. `source.type` and
  `Source.Type` are `null` for unclassified sources.
- Models that tolerate new keys and old data: `Optional<T>` for keys that depend on the plan or
  `include` (absent, `null`, value), open vocabularies as `readonly record struct`s with `...Values`
  constants, raw JSON through `GetRawJson()`, strict `...Time` accessors, skipped entries per page.
- Errors: `FinlightException` with `Kind`; `FinlightApiException` with one subclass per problem code;
  `FinlightValidationException`, `FinlightNetworkException`, `FinlightTimeoutException`,
  `FinlightDecodeException`, `WebhookVerificationException`, `PreemptedException`.
- Retries: 5xx, network errors and timeouts with backoff; a burst 429 waits `Retry-After` (10 s without
  it) and retries once (`RetryOnBurst`); a monthly 429 fails at once. One `X-Request-Id` per call.
- Streams: `StreamAsync` and `RawStreamAsync` as `IAsyncEnumerable<StreamEvent>` with ping, watchdog,
  rotation, reconnects, duplicate filtering and hooks (`OnAdmitted`, `OnReconnect`, `OnError`,
  `OnQuotaWait`). An exhausted WebSocket quota retries every hour by default (`QuotaReconnect`).
  The 115-minute renewal survives the preempt of its own old connection under `Takeover`. A refused or
  failed renewal dial (for example a plan at its connection limit) closes the old connection, waits for
  its close and dials again at once without `OnError`; the usual wait applies only when that dial is
  refused too. The reconnect backoff starts over only after a pong or an article, not on
  `connection.admitted`.
  `FinlightClient.Dispose()` closes every stream of the client.
- The SDK's HTTP handlers never follow redirects, so the API key header cannot reach another host.
- Webhooks: `Webhooks.ConstructEvent` and `Webhooks.VerifySignature`; a correctly signed v2 body is a
  `WrongPayloadVersion` result, not an error.
- Every request sends `User-Agent: finlight-client-dotnet/<version>` and `x-client-version`.

Removed:

- Everything of API v1 and v2: `FetchArticlesAsync`, `FetchArticleByLinkAsync`, `GetSourcesAsync`,
  `GetArticlesParams`, the WebSocket clients, `FinlightWebhooks`, `FinlightBlockedException`, the
  closed enums and the global `JsonStringEnumConverter`.
- Pre-release only (contract kit 3.0.0-rc.6): the SDK's own company cache. `FinlightConfig.CompanyCacheSize`
  and `CacheInfo.NotModified` are gone; `Companies.GetAsync` sends `If-None-Match` only with your
  `IfNoneMatch` and never answers a 304 with a stored record.
