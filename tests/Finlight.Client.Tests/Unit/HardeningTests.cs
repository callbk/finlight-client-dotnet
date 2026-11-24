using System.Text.Json;
using Finlight.Internal;
using Finlight.Tests.Support;

namespace Finlight.Tests.Unit;

/// <summary>Server input and settings that must never escape as raw .NET errors or wrong values.</summary>
public sealed class HardeningTests
{
    private static MockResponse Burst(string retryAfter)
    {
        var burst = MockResponse.Json(429, """{"type":"t","title":"x","status":429,"code":"RATE_LIMITED","detail":"d","limitType":"burst"}""", "application/problem+json");
        return burst with { Headers = new Dictionary<string, string>(burst.Headers) { ["retry-after"] = retryAfter } };
    }

    private static MockResponse EmptyPage()
        => MockResponse.Json(200, """{"articles":[],"pagination":{"limit":1,"returned":0,"hasMore":false,"nextCursor":null},"meta":{}}""");

    [Theory]
    [InlineData("99999999999999")]
    [InlineData("5000000")]
    [InlineData("99999999999999999999999999999999999999")]
    [InlineData("Fri, 31 Dec 9999 23:59:59 GMT")]
    public async Task RetryAfter_OutOfRangeFallsBackToBurstWait(string retryAfter)
    {
        await using var rest = await Rest.StartAsync();
        rest.Mock.Enqueue(Burst(retryAfter), EmptyPage());
        var call = rest.Client.Articles.SearchAsync(new SearchParams());

        // The only timer the call starts is the burst wait; it must be BurstWait, not the header value.
        var wait = await rest.Clock.NextTimerAsync();
        Assert.Equal(rest.Config.BurstWait, wait);
        Assert.Single(rest.Mock.Requests);
        rest.Clock.Advance(wait);
        var page = await call.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Empty(page.Articles);
        Assert.Equal(2, rest.Mock.Requests.Count);

        // Without the retry the header is still readable, cut to the timer limit.
        await using var off = await Rest.StartAsync(c => c with { RetryOnBurst = RetryOnBurst.Off });
        off.Mock.Enqueue(Burst(retryAfter));
        var error = await Assert.ThrowsAsync<RateLimitedException>(() => off.Client.Articles.SearchAsync(new SearchParams()));
        Assert.NotNull(error.RetryAfter);
        Assert.True(error.RetryAfter > TimeSpan.FromHours(1));
        Assert.True(error.RetryAfter <= Limits.MaxWait);
    }

    [Fact]
    public void Config_DurationsAreChecked()
    {
        FinlightConfig Base() => new() { ApiKey = "k" };
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(Base() with { Timeout = TimeSpan.FromSeconds(-1) }));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(Base() with { BurstWait = TimeSpan.FromDays(60) }));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(Base() with { QuotaReconnectInterval = TimeSpan.FromDays(60) }));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(Base() with { ReconnectBaseDelay = TimeSpan.Zero }));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(Base() with { TotalTimeout = TimeSpan.Zero }));
        Assert.Throws<FinlightValidationException>(() => new FinlightClient(Base() with { LongBackoff = TimeSpan.FromDays(-1) }));
        using var infinite = new FinlightClient(Base() with { Timeout = Timeout.InfiniteTimeSpan, BurstWait = TimeSpan.Zero });
    }

    [Fact]
    public async Task Redirects_AreNotFollowed()
    {
        await using var other = await HttpMock.StartAsync();
        await using var rest = await Rest.StartAsync(c => c with { MaxRetries = 0 });
        var location = other.Url.Replace("127.0.0.1", "localhost", StringComparison.Ordinal) + "/elsewhere";
        rest.Mock.Enqueue(new MockResponse(302, new Dictionary<string, string> { ["location"] = location }, []));
        var error = await Assert.ThrowsAsync<FinlightDecodeException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
        Assert.Contains("302", error.Message, StringComparison.Ordinal);
        await Wait.SettleAsync();
        Assert.Empty(other.Requests);
        Assert.Single(rest.Mock.Requests);
    }

    [Fact]
    public async Task Models_NullForAPlainValueOrInsideAListFailsTheEntry()
    {
        // hasContent is a bool: null must not become false.
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Source>("""{"domain":"d","hasContent":null}"""));
        var present = JsonSerializer.Deserialize<Source>("""{"domain":"d","hasContent":false}""")!;
        Assert.True(present.HasContent.IsPresent);
        Assert.False(present.HasContent.Value);
        Assert.False(JsonSerializer.Deserialize<Source>("""{"domain":"d"}""")!.HasContent.IsPresent);

        const string Head = "\"id\":\"i\",\"link\":\"l\",\"source\":{\"domain\":\"d\"},\"title\":\"t\"";
        foreach (var list in new[] { "\"media\":[null]", "\"companies\":[null]", "\"countries\":[null]", "\"events\":[null]", "\"categories\":[\"a\",null]" })
        {
            Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<Article>("{" + Head + "," + list + "}"));
        }

        // Explicit null lists stay what they were: [] for plain lists, "present with null" for Optional ones.
        var nulls = JsonSerializer.Deserialize<Article>("{" + Head + ",\"media\":null,\"companies\":null}")!;
        Assert.Empty(nulls.Media);
        Assert.True(nulls.Companies.IsPresent);
        Assert.Null(nulls.Companies.Value);

        await using var rest = await Rest.StartAsync();
        rest.Mock.Enqueue(MockResponse.Json(200, """{"sources":[{"domain":"a","hasContent":null},{"domain":"b","hasContent":true}]}"""));
        var sources = await rest.Client.Sources.ListAsync();
        Assert.Equal("b", Assert.Single(sources).Domain);
        Assert.Equal(0, Assert.Single(sources.Skipped).Index);

        rest.Mock.Enqueue(MockResponse.Json(200, "{\"articles\":[{" + Head + ",\"media\":[null]}],\"pagination\":{\"limit\":1,\"returned\":1,\"hasMore\":false,\"nextCursor\":null},\"meta\":{}}"));
        var page = await rest.Client.Articles.SearchAsync(new SearchParams());
        Assert.Empty(page.Articles);
        Assert.Single(page.Skipped);

        rest.Mock.Enqueue(MockResponse.Json(200, "{\"article\":{" + Head + ",\"companies\":[null]}}"));
        await Assert.ThrowsAsync<FinlightDecodeException>(() => rest.Client.Articles.GetAsync("i"));
    }

    [Fact]
    public async Task SearchAll_CancellationCarriesTheCallersToken()
    {
        var error500 = MockResponse.Json(500, """{"type":"t","title":"x","status":500,"code":"INTERNAL_ERROR","detail":"d"}""", "application/problem+json");

        // The token passed to SearchAllAsync.
        await using (var rest = await Rest.StartAsync())
        {
            rest.Mock.Enqueue(error500);
            using var cts = new CancellationTokenSource();
            var run = Task.Run(async () =>
            {
                await foreach (var _ in rest.Client.Articles.SearchAllAsync(new SearchParams(), null, cts.Token))
                {
                }
            });
            await Wait.ForAsync(() => rest.Mock.Requests.Count == 1, "the first attempt");
            await cts.CancelAsync();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(cts.Token, canceled.CancellationToken);
        }

        // The token passed to the enumerator.
        await using (var rest = await Rest.StartAsync())
        {
            rest.Mock.Enqueue(error500);
            using var cts = new CancellationTokenSource();
            var run = Task.Run(async () =>
            {
                await foreach (var _ in rest.Client.Articles.SearchAllAsync(new SearchParams()).WithCancellation(cts.Token))
                {
                }
            });
            await Wait.ForAsync(() => rest.Mock.Requests.Count == 1, "the first attempt");
            await cts.CancelAsync();
            var canceled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run);
            Assert.Equal(cts.Token, canceled.CancellationToken);
        }
    }

    [Fact]
    public async Task CompanyCache_ARevalidationDoesNotChangeEarlierRecords()
    {
        await using var rest = await Rest.StartAsync();
        var ok = MockResponse.Json(200, """{"company":{"companyId":7,"name":"Seven"}}""");
        rest.Mock.Enqueue(
            ok with { Headers = new Dictionary<string, string>(ok.Headers) { ["etag"] = "\"v1\"" } },
            new MockResponse(304, new Dictionary<string, string> { ["etag"] = "\"v1\"" }, []));
        var first = (await rest.Client.Companies.GetAsync(7))!;
        Assert.False(first.GetCacheInfo()!.NotModified);
        var second = (await rest.Client.Companies.GetAsync(7))!;
        Assert.True(second.GetCacheInfo()!.NotModified);
        Assert.Equal("Seven", second.Name);
        Assert.Equal(first.GetRawJson(), second.GetRawJson());
        Assert.NotSame(first, second);
        Assert.False(first.GetCacheInfo()!.NotModified);
        Assert.Equal("\"v1\"", rest.Mock.Requests[1].Header("If-None-Match"));
    }
}
