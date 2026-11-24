using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Finlight.Internal;
using Finlight.Tests.Support;
using Microsoft.Extensions.Logging;

namespace Finlight.Tests.Unit;

/// <summary>Client rules for requests, models, errors and configuration beyond the kit cases.</summary>
public sealed class RestRulesTests
{
    private static readonly string[] LegacyKeys = ["includeContent", "includeEntities", "extended", "pageSize", "publishDate", "isUpdate"];

    [Fact]
    public void Scope_NoRequestTypeCarriesALegacyKey()
    {
        var full = new SearchParams
        {
            Query = "q",
            Sources = ["a"],
            OptInSources = ["b"],
            ExcludeSources = ["c"],
            From = "2026-01-01",
            To = DateTimeOffset.UnixEpoch,
            Languages = ["en"],
            Tickers = ["X"],
            Countries = ["US"],
            Categories = ["markets"],
            HasContent = false,
            Roles = [CompanyRole.Subject],
            MinRelevance = 0,
            EventTypes = [EventType.Mna],
            EventDirections = [EventDirection.Positive],
            MinImportance = Importance.High,
            Sectors = ["s"],
            CompanyCountries = ["US"],
            Exchanges = ["XNAS"],
            OrderBy = OrderBy.AvailableAt,
            Order = SortOrder.Asc,
            Limit = 1,
            Cursor = "c",
            Include = [IncludeBlock.Content, IncludeBlock.Companies, IncludeBlock.Events],
        };
        var bodies = new[]
        {
            Requests.SearchBody(full).ToJsonString(),
            Requests.StreamSubscribe(new StreamParams
            {
                Query = "q", Sources = ["a"], ExcludeSources = ["b"], OptInSources = ["c"], Language = "en", Tickers = ["X"], Countries = ["US"],
                Categories = ["m"], ExcludeEmptyContent = false, IncludeUpdates = false, Include = [IncludeBlock.Content],
            }).ToJsonString(),
            Requests.RawSubscribe(new RawStreamParams { Query = "q", Sources = ["a"], ExcludeSources = ["b"], Language = "en", IncludeUpdates = true }).ToJsonString(),
        };
        foreach (var body in bodies)
        {
            foreach (var key in LegacyKeys)
            {
                Assert.DoesNotContain($"\"{key}\"", body, StringComparison.Ordinal);
            }
        }

        // Every set key is sent, explicit false and 0 included.
        var search = JsonNode.Parse(bodies[0])!.AsObject();
        Assert.Equal(24, search.Count);
        Assert.False(search["hasContent"]!.GetValue<bool>());
        Assert.Equal(0, search["minRelevance"]!.GetValue<double>());
        Assert.Equal("1970-01-01T00:00:00.000Z", search["to"]!.GetValue<string>());
    }

    [Fact]
    public void Scope_SourceHasNoV1OrV2Paths()
    {
        var src = Path.Combine(Kit.RepoRoot, "src", "Finlight.Client");
        var allowed = new[] { Path.Combine("Internal", "Frames.cs"), "Webhooks.cs" };
        var patterns = new List<string> { "/v1/", "/v2/", "sendArticle", "admin_kick" };
        foreach (var key in LegacyKeys)
        {
            patterns.Add(key);
            patterns.Add(Regex.Replace(key, "([A-Z])", "_$1"));
        }

        var hits = new List<string>();
        foreach (var file in Directory.EnumerateFiles(src, "*.cs", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(src, file);
            if (rel.StartsWith("obj", StringComparison.Ordinal) || rel.StartsWith("bin", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            foreach (var pattern in patterns)
            {
                if (text.Contains(pattern, StringComparison.OrdinalIgnoreCase) && !allowed.Contains(rel))
                {
                    hits.Add($"{rel}: {pattern}");
                }
            }
        }

        Assert.Empty(hits);
    }

    [Fact]
    public void Names_PublicSurfaceMatchesTheNameTable()
    {
        string[] vocab =
        [
            "IncludeBlock", "OrderBy", "SortOrder", "CompanyRole", "EventType", "EventDirection", "Importance", "SentimentLabel", "SourceType",
            "StreamEventName", "ConnectionErrorCode", "PreemptReason", "WebhookEventName", "ProblemCode", "RateLimitType", "FinlightErrorKind",
            "ApiErrorKind", "WebhookVerificationReason", "ReconnectReason", "StreamEventKind",
        ];
        string[] expected =
        [
            "FinlightClient", "FinlightConfig", "RetryOnBurst", "ArticlesService", "CompaniesService", "SourcesService", "SearchAllResult",
            "SearchParams", "StreamParams", "RawStreamParams", "CompanySearchParams", "LookupOptions", "CompanyGetOptions", "SearchAllOptions", "DateInput",
            "Article", "ArticleSource", "Media", "Sentiment", "Company", "Listing", "EventTag", "CompanyRecord", "Source", "Pagination", "ArticlePage",
            "SkippedEntry", "CompanyList", "SourceList", "CacheInfo", "Optional`1",
            "StreamEvent", "AdmittedInfo", "ReconnectInfo", "QuotaWaitInfo", "StreamOptions", "FinlightStream",
            "Webhooks", "WebhookEvent", "WrongPayloadVersion", "WebhookPayload", "WebhookVerifyOptions",
            "FinlightException", "FinlightApiException", "ValidationFailedException", "CursorInvalidException", "CursorMismatchException",
            "UnauthorizedException", "ForbiddenException", "NotFoundException", "RateLimitedException", "InternalServerException",
            "FinlightValidationException", "FinlightNetworkException", "FinlightTimeoutException", "FinlightDecodeException",
            "WebhookVerificationException", "PreemptedException", "FieldIssue",
            .. vocab, .. vocab.Select(v => v + "Values"),
        ];
        var actual = typeof(FinlightClient).Assembly.GetExportedTypes().Select(t => t.Name).Order().ToList();
        Assert.Equal(expected.Order().ToList(), actual);
        Assert.All(typeof(FinlightClient).Assembly.GetExportedTypes(), t => Assert.Equal("Finlight", t.Namespace));
        Assert.Equal(3, FinlightClient.PayloadVersion);
        Assert.Equal("2.0.0", FinlightClient.Version);

        // Presence checks and parsed times use the names of the table.
        foreach (var name in new[] { "ContentPresent", "SentimentPresent", "CompaniesPresent", "EventsPresent", "PublishedAtTime", "FirstSeenAtTime", "RevisedAtTime", "AvailableAtTime" })
        {
            Assert.NotNull(typeof(Article).GetProperty(name));
        }
    }

    [Theory]
    [InlineData("de-DE")]
    [InlineData("tr-TR")]
    public async Task Requests_DoNotDependOnTheCulture(string culture)
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = new CultureInfo(culture);
        try
        {
            await using var rest = await Rest.StartAsync();
            rest.Mock.Enqueue(MockResponse.Json(200, """{"articles":[],"pagination":{"limit":5,"returned":0,"hasMore":false,"nextCursor":null}}"""));
            await rest.Client.Articles.SearchAsync(new SearchParams
            {
                MinRelevance = 0.5,
                From = new DateTimeOffset(2026, 11, 1, 1, 2, 3, 456, TimeSpan.FromHours(2)),
                To = new DateOnly(2026, 11, 23),
                Include = [new IncludeBlock("INCLUDE")],
                Limit = 5,
            });
            rest.Mock.Enqueue(MockResponse.Json(200, """{"companies":[]}"""));
            await rest.Client.Companies.GetManyAsync([1, 2]);
            var body = rest.Mock.Requests[0].BodyText;
            Assert.Contains("\"minRelevance\":0.5", body, StringComparison.Ordinal);
            Assert.Contains("\"from\":\"2026-10-31T23:02:03.456Z\"", body, StringComparison.Ordinal);
            Assert.Contains("\"to\":\"2026-11-23\"", body, StringComparison.Ordinal);
            Assert.Contains("\"include\":[\"INCLUDE\"]", body, StringComparison.Ordinal);
            Assert.Equal("ids=1%2C2", rest.Mock.Requests[1].RawQuery);
        }
        finally
        {
            CultureInfo.CurrentCulture = CultureInfo.CurrentUICulture = previous;
        }
    }

    [Fact]
    public async Task Requests_EmptySearchSendsAnEmptyObjectAndEmptyIncludeIsDropped()
    {
        await using var rest = await Rest.StartAsync();
        rest.Mock.Enqueue(
            MockResponse.Json(200, """{"articles":[],"pagination":{"limit":20,"returned":0,"hasMore":false,"nextCursor":null}}"""),
            MockResponse.Json(200, """{"article":{"id":"i","link":"l","source":{"domain":"d"},"title":"t"}}"""));
        await rest.Client.Articles.SearchAsync(new SearchParams { Include = [] });
        Assert.Equal("{}", rest.Mock.Requests[0].BodyText);
        await rest.Client.Articles.GetByLinkAsync("https://x.example/a b?c=d&e", new LookupOptions { Include = [] });
        Assert.Equal("link=https%3A%2F%2Fx.example%2Fa%20b%3Fc%3Dd%26e", rest.Mock.Requests[1].RawQuery);
    }

    [Fact]
    public async Task Requests_ShapeErrorsFailBeforeAnyRequest()
    {
        await using var rest = await Rest.StartAsync();
        var articles = rest.Client.Articles;
        var companies = rest.Client.Companies;
        var cases = new (string Param, Func<Task> Call)[]
        {
            ("limit", () => articles.SearchAsync(new SearchParams { Limit = 0 })),
            ("limit", () => articles.SearchAsync(new SearchParams { Limit = 101 })),
            ("languages", () => articles.SearchAsync(new SearchParams { Languages = [] })),
            ("minRelevance", () => articles.SearchAsync(new SearchParams { MinRelevance = 1.5 })),
            ("minRelevance", () => articles.SearchAsync(new SearchParams { MinRelevance = double.NaN })),
            ("include", () => articles.SearchAsync(new SearchParams { Include = [IncludeBlock.Content, IncludeBlock.Companies, IncludeBlock.Events, IncludeBlock.Content] })),
            ("from", () => articles.SearchAsync(new SearchParams { From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified) })),
            ("link", () => articles.GetByLinkAsync(string.Empty)),
            ("id", () => articles.GetAsync(string.Empty)),
            ("q", () => companies.SearchAsync(new CompanySearchParams { Q = new string('x', 201) })),
            ("mic", () => companies.SearchAsync(new CompanySearchParams { Q = "X", Mic = new string('x', 33) })),
            ("ids", () => companies.GetManyAsync([])),
            ("ids", () => companies.GetManyAsync(Enumerable.Range(1, 11).Select(i => (long)i))),
            ("companyId", () => companies.GetAsync(-1)),
            ("maxPages", () => Task.FromResult(articles.SearchAllAsync(new SearchParams(), new SearchAllOptions { MaxPages = 0 }))),
        };
        foreach (var (param, call) in cases)
        {
            var error = await Assert.ThrowsAsync<FinlightValidationException>(call);
            Assert.Equal(param, error.Param);
            Assert.Equal("validation", error.Kind.Value);
        }

        // searchAll and streams check at the call, before any iteration.
        Assert.Throws<FinlightValidationException>(() => articles.SearchAllAsync(new SearchParams { Limit = 0 }));
        Assert.Throws<FinlightValidationException>(() => rest.Client.StreamAsync(new StreamParams { Include = [IncludeBlock.Content, IncludeBlock.Content, IncludeBlock.Content, IncludeBlock.Content] }));
        Assert.Empty(rest.Mock.Requests);

        // Vocabulary values are not checked: an unknown event type goes out unchanged.
        rest.Mock.Enqueue(MockResponse.Json(200, """{"articles":[],"pagination":{"limit":20,"returned":0,"hasMore":false,"nextCursor":null}}"""));
        await articles.SearchAsync(new SearchParams { EventTypes = ["earnigns"] });
        Assert.Equal("""{"eventTypes":["earnigns"]}""", rest.Mock.Requests[0].BodyText);
    }

    [Fact]
    public async Task Pager_StopsAtMaxPagesAndKeepsTheCursor()
    {
        var logs = new CapturingLoggerFactory();
        await using var rest = await Rest.StartAsync(c => c with { MaxPages = 2, LoggerFactory = logs });
        for (var i = 0; i < 3; i++)
        {
            rest.Mock.Enqueue(MockResponse.Json(200, $$$"""{"articles":[{"id":"a{{{i}}}","link":"l{{{i}}}","source":{"domain":"d"},"title":"t"}],"pagination":{"limit":1,"returned":1,"hasMore":true,"nextCursor":"c{{{i}}}"}}"""));
        }

        var result = rest.Client.Articles.SearchAllAsync(new SearchParams { Limit = 1 });
        var pages = new List<ArticlePage>();
        await foreach (var page in result.Pages())
        {
            pages.Add(page);
        }

        Assert.Equal(2, pages.Count);
        Assert.True(result.Truncated);
        Assert.Equal("c1", result.NextCursor);
        Assert.Equal(2, result.PagesFetched);
        Assert.Equal(2, rest.Mock.Requests.Count);
        Assert.Contains(logs.Lines, l => l.Level == LogLevel.Warning && l.Text.Contains("maxPages", StringComparison.Ordinal));
        Assert.Throws<FinlightValidationException>(() => result.GetAsyncEnumerator());
    }

    [Fact]
    public async Task Models_ABrokenEntryIsSkippedAndALookupWithoutLinkFails()
    {
        await using var rest = await Rest.StartAsync();
        rest.Mock.Enqueue(MockResponse.Json(200, """
            {"articles":[
              {"id":"a","link":"l","source":{"domain":"d"},"title":"t","sentiment":{"label":"positive","confidence":"0.95"}},
              {"id":"b","link":"l2","source":{"domain":"d"},"title":"t2","firstSeenAt":null,"summary":"","publishedAt":"now","newKey":{"x":1}},
              {"id":"c","source":{"domain":"d"},"title":"t3"}
            ],"pagination":{"limit":3,"returned":3,"hasMore":false,"nextCursor":null},"meta":{}}
            """));
        var page = await rest.Client.Articles.SearchAsync(new SearchParams());
        var article = Assert.Single(page.Articles);
        Assert.Equal("b", article.Id);
        Assert.Equal(string.Empty, article.Summary);
        Assert.Equal("now", article.PublishedAt);
        Assert.Null(article.PublishedAtTime);
        Assert.Equal([0, 2], page.Skipped.Select(s => s.Index));
        Assert.Contains("0.95", page.Skipped[0].Raw, StringComparison.Ordinal);

        rest.Mock.Enqueue(MockResponse.Json(200, """{"article":{"id":"a","source":{"domain":"d"},"title":"t"}}"""));
        var error = await Assert.ThrowsAsync<FinlightDecodeException>(() => rest.Client.Articles.GetAsync("a"));
        Assert.Equal("decode", error.Kind.Value);
        Assert.NotNull(error.Raw);
    }

    [Theory]
    [InlineData("2026-11-23T21:04:05.123Z", true)]
    [InlineData("2026-11-23T21:04:05Z", true)]
    [InlineData("2026-11-23T22:04:05.123456789+01:00", true)]
    [InlineData("2026-11-23T21:04:05.123", false)]
    [InlineData("2026-11-23", false)]
    [InlineData("1795467845", false)]
    [InlineData("now", false)]
    [InlineData("", false)]
    [InlineData("2026-13-45T99:99:99Z", false)]
    public void Models_TimesParseStrictly(string text, bool ok)
    {
        var parsed = Rfc3339.Parse(text);
        Assert.Equal(ok, parsed is not null);
        if (ok)
        {
            Assert.Equal(new DateTimeOffset(2026, 11, 23, 21, 4, 5, TimeSpan.Zero), parsed!.Value.AddTicks(-(parsed.Value.Ticks % TimeSpan.TicksPerSecond)));
        }
    }

    [Fact]
    public void Models_NumbersKeepFullRange()
    {
        var article = JsonSerializer.Deserialize<Article>("""
            {"id":"i","link":"l","source":{"domain":"d"},"title":"t","companies":[{"companyId":9007199254740991,"name":"n","confidence":0.912345678}],
             "events":[{"type":"mna","companyIds":[9007199254740991]}]}
            """)!;
        Assert.Equal(9007199254740991L, article.Companies.Value![0].CompanyId);
        Assert.Equal(0.912345678, article.Companies.Value![0].Confidence);
        Assert.Equal(9007199254740991L, article.Events.Value![0].CompanyIds.Value![0]);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Article>("""{"id":"i","link":"l","source":{"domain":"d"},"title":null}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Article>("""{"id":"i","link":"l","source":{"domain":"d"},"title":"t","media":[{"url":1}]}"""));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Company>("""{"companyId":"1","name":"n"}"""));
    }

    [Fact]
    public async Task Errors_NestJsArrayAndRetriesKeepTheRequestId()
    {
        await using var rest = await Rest.StartAsync();
        rest.Mock.Enqueue(MockResponse.Json(400, """{"statusCode":400,"message":["a is wrong","b is wrong"],"error":"Bad Request"}"""));
        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
        Assert.Null(error.Code);
        Assert.Equal(["a is wrong", "b is wrong"], error.Errors.Select(e => e.Issue));
        Assert.Equal(rest.Mock.Requests[0].Header("X-Request-Id"), error.RequestId);

        // A 429 classified from the detail text when limitType is missing; monthly is never retried.
        rest.Mock.Enqueue(MockResponse.Json(429, """{"statusCode":429,"message":"Throttling Exception: Exceeded token limit of 5 for the current period."}"""));
        var limited = await Assert.ThrowsAsync<RateLimitedException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
        Assert.Equal(RateLimitType.Monthly, limited.LimitType);
        rest.Mock.Enqueue(MockResponse.Json(429, """{"statusCode":429,"message":"slow down"}"""));
        limited = await Assert.ThrowsAsync<RateLimitedException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
        Assert.Equal(RateLimitType.Unknown, limited.LimitType);
        Assert.Equal(3, rest.Mock.Requests.Count);
    }

    [Fact]
    public async Task Retry_NetworkErrorsAndTimeoutsAreRetriedThenWrapped()
    {
        // A closed port: every attempt fails to connect.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        await using (var rest = await Rest.StartAsync(c => c with { BaseUrl = $"http://127.0.0.1:{port}" }))
        {
            var error = await Assert.ThrowsAsync<FinlightNetworkException>(() => rest.RunAsync(() => rest.Client.Sources.ListAsync()));
            Assert.Equal("network", error.Kind.Value);
            Assert.IsType<HttpRequestException>(error.InnerException);
        }

        // A server that never answers: each attempt times out.
        await using (var rest = await Rest.StartAsync(c => c with { Timeout = TimeSpan.FromMilliseconds(100), MaxRetries = 1 }))
        {
            var slow = new TcpListener(IPAddress.Loopback, 0);
            slow.Start();
            try
            {
                var slowPort = ((IPEndPoint)slow.LocalEndpoint).Port;
                using var client = new FinlightClient(rest.Config with { BaseUrl = $"http://127.0.0.1:{slowPort}" });
                var error = await Assert.ThrowsAsync<FinlightTimeoutException>(() => rest.RunAsync(() => client.Sources.ListAsync()));
                Assert.Equal("timeout", error.Kind.Value);
                Assert.IsAssignableFrom<FinlightNetworkException>(error);
            }
            finally
            {
                slow.Stop();
            }
        }
    }

    [Fact]
    public async Task Retry_CancellationArrivesUnchanged()
    {
        await using var rest = await Rest.StartAsync();
        rest.Mock.Enqueue(MockResponse.Json(500, """{"type":"t","title":"x","status":500,"code":"INTERNAL_ERROR","detail":"The request could not be processed."}""", "application/problem+json"));
        using var cts = new CancellationTokenSource();
        var call = rest.Client.Articles.SearchAsync(new SearchParams(), cts.Token);
        await Wait.ForAsync(() => rest.Mock.Requests.Count == 1, "the first attempt");
        await cts.CancelAsync();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call);
        Assert.Equal(cts.Token, error.CancellationToken);
        Assert.Single(rest.Mock.Requests);
    }

    [Fact]
    public async Task Retry_TotalTimeoutLimitsTheWaits()
    {
        await using var rest = await Rest.StartAsync(c => c with { TotalTimeout = TimeSpan.FromSeconds(5) });
        var burst = MockResponse.Json(429, """{"type":"t","title":"x","status":429,"code":"RATE_LIMITED","detail":"d","limitType":"burst"}""", "application/problem+json");
        rest.Mock.Enqueue(burst with { Headers = new Dictionary<string, string>(burst.Headers) { ["retry-after"] = "9" } });
        var error = await Assert.ThrowsAsync<FinlightTimeoutException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
        Assert.IsType<RateLimitedException>(error.InnerException);
        Assert.Single(rest.Mock.Requests);
    }

    [Fact]
    public async Task Config_UrlsWithTrailingSlashAndAnInjectedClient()
    {
        await using var mock = await HttpMock.StartAsync();
        mock.Enqueue(MockResponse.Json(200, """{"sources":[]}"""));
        using var http = new HttpClient();
        using (var client = new FinlightClient(new FinlightConfig { ApiKey = "k", BaseUrl = mock.Url + "///", HttpClient = http }))
        {
            Assert.Empty(await client.Sources.ListAsync());
        }

        Assert.Equal("/v3/sources", Assert.Single(mock.Requests).Path);
        Assert.Equal("k", mock.Requests[0].Header("X-API-KEY"));

        // The SDK never disposes a client it does not own.
        mock.Enqueue(MockResponse.Json(200, "ok"));
        Assert.Equal("ok", await http.GetStringAsync(mock.Url));

        Assert.Throws<FinlightValidationException>(() => new FinlightClient(string.Empty));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(new FinlightConfig { ApiKey = " " }));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(new FinlightConfig { ApiKey = "k", MaxMessageBytes = 1024 }));
        var defaults = new FinlightConfig { ApiKey = "secret-key" };
        Assert.Equal("https://api.finlight.me", defaults.BaseUrl);
        Assert.Equal("wss://wss.finlight.me", defaults.WssUrl);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.Timeout);
        Assert.Equal(3, defaults.MaxRetries);
        Assert.Equal(TimeSpan.FromMilliseconds(500), defaults.RetryBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(8), defaults.RetryMaxDelay);
        Assert.Equal(RetryOnBurst.Once, defaults.RetryOnBurst);
        Assert.Equal(TimeSpan.FromSeconds(10), defaults.BurstWait);
        Assert.Equal(10, defaults.MaxPages);
        Assert.Equal(32L * 1024 * 1024, defaults.MaxResponseBytes);
        Assert.Equal(256, defaults.CompanyCacheSize);
        Assert.Equal(TimeSpan.FromSeconds(25), defaults.PingInterval);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.WatchdogTimeout);
        Assert.Equal(TimeSpan.FromMinutes(115), defaults.RotationInterval);
        Assert.Equal(TimeSpan.FromSeconds(1), defaults.ReconnectBaseDelay);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.ReconnectMaxDelay);
        Assert.Equal(TimeSpan.FromSeconds(60), defaults.LongBackoff);
        Assert.True(defaults.QuotaReconnect);
        Assert.Equal(TimeSpan.FromHours(1), defaults.QuotaReconnectInterval);
        Assert.Equal(5, defaults.SearchFailedThreshold);
        Assert.Equal(1000, defaults.DedupeCacheSize);
        Assert.Equal(32L * 1024 * 1024, defaults.MaxMessageBytes);
        Assert.Equal(TimeSpan.FromSeconds(30), defaults.ConnectTimeout);
        Assert.DoesNotContain("secret-key", defaults.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("secret-key", new FinlightClient(defaults).ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Logging_NeverShowsTheKey()
    {
        const string key = "sk_secret_test_key_0123456789";
        var logs = new CapturingLoggerFactory();
        await using var rest = await Rest.StartAsync(c => c with { LoggerFactory = logs, MaxRetries = 1 }, apiKey: key);
        rest.Mock.Enqueue(
            MockResponse.Json(401, """{"type":"t","title":"Unauthorized","status":401,"code":"UNAUTHORIZED","detail":"Unauthorized","requestId":"r"}""", "application/problem+json"),
            MockResponse.Json(502, "<html>bad</html>", "text/html"),
            MockResponse.Json(502, "<html>bad</html>", "text/html"));
        var errors = new List<Exception>
        {
            await Assert.ThrowsAnyAsync<FinlightException>(() => rest.Client.Articles.SearchAsync(new SearchParams())),
            await Assert.ThrowsAnyAsync<FinlightException>(() => rest.RunAsync(() => rest.Client.Articles.SearchAsync(new SearchParams()))),
        };
        Assert.NotEmpty(logs.Lines);
        Assert.All(logs.Lines, l => Assert.Equal("Finlight", l.Category));
        foreach (var text in logs.Lines.Select(l => l.Text).Concat(errors.Select(e => e.ToString())))
        {
            Assert.DoesNotContain(key, text, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Config_ResponsesHaveASizeLimit()
    {
        await using var rest = await Rest.StartAsync(c => c with { MaxResponseBytes = 64 });
        rest.Mock.Enqueue(MockResponse.Json(200, "{\"sources\":[" + string.Join(',', Enumerable.Repeat("{\"domain\":\"x.example\"}", 20)) + "]}"));
        var error = await Assert.ThrowsAsync<FinlightDecodeException>(() => rest.Client.Sources.ListAsync());
        Assert.Contains("MaxResponseBytes", error.Message, StringComparison.Ordinal);
        await Assert.ThrowsAsync<FinlightValidationException>(() => rest.Client.Articles.SearchAsync(null!));
    }

    [Fact]
    public async Task Client_InstallsNoGlobalHandlers()
    {
        static int Handlers(string name)
            => (typeof(AppContext).GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
                ?.GetValue(null) as Delegate)?.GetInvocationList().Length ?? 0;

        var before = (Handlers("ProcessExit"), Handlers("UnhandledException"), Console.Out, Console.Error);
        await using var ws = await WsMock.StartAsync();
        await using (var probe = new StreamProbe(ws).Start(false, [], Kit.ApiKey, "n"))
        {
            await (await ws.NextDialAsync()).Conn!.NextAsync("subscribe");
            Assert.Equal(before, (Handlers("ProcessExit"), Handlers("UnhandledException"), Console.Out, Console.Error));
        }

        Assert.Equal(before, (Handlers("ProcessExit"), Handlers("UnhandledException"), Console.Out, Console.Error));
    }
}
