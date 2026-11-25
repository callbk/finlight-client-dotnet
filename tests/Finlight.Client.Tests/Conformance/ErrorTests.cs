using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Conformance;

/// <summary>E-01 to E-25: error bodies, error kinds and retry classes.</summary>
public sealed class ErrorTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["E-01"] = "validation-enum.json",
        ["E-02"] = "validation-multiple.json",
        ["E-03"] = "validation-query-language.json",
        ["E-04"] = "validation-empty-languages.json",
        ["E-05"] = "validation-include-query.json",
        ["E-06"] = "companies-neither.json",
        ["E-07"] = "companies-both.json",
        ["E-08"] = "companies-ids-not-integer.json",
        ["E-09"] = "company-id-not-integer.json",
        ["E-10"] = "cursor-invalid.json",
        ["E-11"] = "cursor-version.json",
        ["E-12"] = "unauthorized-missing-key.json",
        ["E-13"] = "unauthorized-invalid-key.json",
        ["E-14"] = "forbidden-companies.json",
        ["E-15"] = "not-found-id.json",
        ["E-16"] = "not-found-link.json",
        ["E-17"] = "not-found-company.json",
        ["E-18"] = "rate-limited-monthly.json",
        ["E-19"] = "rate-limited-burst.json",
        ["E-20"] = "internal-error.json",
        ["E-21"] = "malformed-json-body.json",
        ["E-22"] = "unknown-route.json",
        ["E-23"] = "generated-request-id.json",
        ["E-24"] = "gateway-non-json.json",
        ["E-25"] = "cursor-mismatch.json",
    };

    public static TheoryData<string> Cases => [.. Files.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string id)
    {
        var fixture = Kit.Obj("fixtures/errors/" + Files[id]);
        Assert.Equal(id, fixture["id"]!.GetValue<string>());
        await using var rest = await Rest.StartAsync();
        var call = Call(id, fixture, rest.Client);
        var expect = fixture["expect"]!;
        var retry = expect["retry"]!.GetValue<string>();
        var attempts = retry switch
        {
            "never" => 1,
            "backoff" => rest.Config.MaxRetries + 1,
            "once-after-burst-wait" => 2,
            _ => throw new InvalidOperationException(retry),
        };
        for (var i = 0; i < attempts; i++)
        {
            rest.Mock.Enqueue(MockResponse.FromFixture(fixture["response"]!));
        }

        var error = await Assert.ThrowsAnyAsync<FinlightApiException>(() => rest.RunAsync(call));
        var requests = rest.Mock.Requests;
        Assert.Equal(attempts, requests.Count);
        Assert.Single(requests.Select(r => r.Header("X-Request-Id")).Distinct());
        CheckError(error, fixture, requests[0].Header("X-Request-Id"));

        switch (id)
        {
            case "E-02":
                Assert.Equal("3 request fields are not valid.", error.Detail);
                Assert.Equal(3, error.Errors.Count);
                Assert.Throws<FinlightValidationException>(() => rest.Client.Articles.SearchAllAsync(new SearchParams { Limit = 500 }));
                break;
            case "E-04":
                Assert.Equal("languages", Assert.Throws<FinlightValidationException>(
                    () => rest.Client.Articles.SearchAllAsync(new SearchParams { Languages = [] })).Param);
                break;
            case "E-06":
                await Assert.ThrowsAsync<FinlightValidationException>(() => rest.Client.Companies.SearchAsync(new CompanySearchParams { Q = string.Empty }));
                break;
            case "E-08":
                await Assert.ThrowsAsync<FinlightValidationException>(() => rest.Client.Companies.GetManyAsync([900001, 0]));
                break;
            case "E-09":
                await Assert.ThrowsAsync<FinlightValidationException>(() => rest.Client.Companies.GetAsync(0));
                break;
            case "E-10":
            case "E-18":
            case "E-25":
                // Inside searchAll the pager stops with the same error at once.
                rest.Mock.Enqueue(MockResponse.FromFixture(fixture["response"]!));
                var result = rest.Client.Articles.SearchAllAsync(new SearchParams());
                var paged = await Assert.ThrowsAnyAsync<FinlightApiException>(async () =>
                {
                    await foreach (var _ in result)
                    {
                    }
                });
                Assert.Equal(error.GetType(), paged.GetType());
                Assert.Equal(attempts + 1, rest.Mock.Requests.Count);
                Assert.Equal(0, result.PagesFetched);
                break;
            case "E-19":
                await BurstAsync(fixture);
                break;
            case "E-23":
                Assert.Throws<FinlightValidationException>(() => rest.Client.Articles.SearchAllAsync(new SearchParams { Limit = 0 }));
                break;
        }
    }

    /// <summary>The SDK call that reaches the fixture's endpoint. Requests the SDK refuses locally use a valid call instead.</summary>
    private static Func<Task> Call(string id, JsonObject fixture, FinlightClient client)
    {
        var request = fixture["request"]!;
        var path = request["path"]!.GetValue<string>();
        var body = request["body"] as JsonObject;
        return id switch
        {
            "E-01" or "E-03" or "E-10" or "E-11" or "E-25" => () => client.Articles.SearchAsync(Rest.Args<SearchParams>(body)),
            "E-05" => () => client.Articles.GetAsync("EU8JwpsZ8iJehuyPZjMqRz", new LookupOptions { Include = [new IncludeBlock("contnet")] }),
            "E-15" => () => client.Articles.GetAsync(path["/v3/articles/".Length..]),
            "E-16" => () => client.Articles.GetByLinkAsync("https://news.example.com/missing"),
            "E-06" or "E-07" or "E-14" => () => client.Companies.SearchAsync(new CompanySearchParams { Q = "XMPL" }),
            "E-08" => () => client.Companies.GetManyAsync([900001]),
            "E-09" => () => client.Companies.GetAsync(1),
            "E-17" => () => client.Companies.GetAsync(999999),
            "E-22" => () => client.Sources.ListAsync(),
            _ => () => client.Articles.SearchAsync(new SearchParams()),
        };
    }

    internal static void CheckError(FinlightApiException error, JsonObject fixture, string sentRequestId)
    {
        var expect = fixture["expect"]!;
        var response = fixture["response"]!;
        var apiKind = expect["apiKind"]!.GetValue<string>();
        Assert.Equal("api", error.Kind.Value);
        Assert.Equal(apiKind, error.ApiKind.Value);
        var expectedType = apiKind switch
        {
            "validation_failed" => typeof(ValidationFailedException),
            "cursor_invalid" => typeof(CursorInvalidException),
            "cursor_mismatch" => typeof(CursorMismatchException),
            "unauthorized" => typeof(UnauthorizedException),
            "forbidden" => typeof(ForbiddenException),
            "not_found" => typeof(NotFoundException),
            "rate_limited" => typeof(RateLimitedException),
            "internal_error" => typeof(InternalServerException),
            _ => typeof(FinlightApiException),
        };
        Assert.IsType(expectedType, error);
        Assert.Equal(response["status"]!.GetValue<int>(), error.Status);
        var code = expect["code"]?.GetValue<string>();
        Assert.Equal(code == "NESTJS_FALLBACK" ? null : code, error.Code);
        var body = response["body"];
        if (body is JsonObject problem)
        {
            Assert.Equal(problem["requestId"]?.GetValue<string>() ?? sentRequestId, error.RequestId);
            Assert.Equal(problem["detail"]?.GetValue<string>() ?? problem["message"]?.GetValue<string>(), error.Detail);
            var issues = problem["errors"]?.AsArray() ?? [];
            if (code != "NESTJS_FALLBACK")
            {
                Assert.Equal(issues.Count, error.Errors.Count);
                for (var i = 0; i < issues.Count; i++)
                {
                    Assert.Equal(issues[i]!["field"]?.GetValue<string>(), error.Errors[i].Field);
                    Assert.Equal(issues[i]!["issue"]?.GetValue<string>(), error.Errors[i].Issue);
                    var allowed = issues[i]!["allowedValues"]?.AsArray().Select(v => v!.GetValue<string>()).ToList();
                    Assert.Equal(allowed, error.Errors[i].AllowedValues);
                }
            }

            Assert.Equal(Kit.Canonical(problem), Kit.Canonical(error.GetRawJson()!));
        }
        else
        {
            // Not JSON: status plus the text, no parse error escapes.
            Assert.Equal(sentRequestId, error.RequestId);
            Assert.Equal(body!.GetValue<string>(), error.Raw);
            Assert.Null(error.GetRawJson());
        }

        if (error is RateLimitedException limited)
        {
            Assert.Equal(body!["limitType"]!.GetValue<string>(), limited.LimitType.Value);
        }
    }

    /// <summary>E-19 in detail: exactly Retry-After, 10 s without it, the switch, and cancellation during the wait.</summary>
    private static async Task BurstAsync(JsonObject fixture)
    {
        var response = MockResponse.FromFixture(fixture["response"]!);

        // Waits exactly Retry-After (7 s), then the retry succeeds with the same request id.
        await using (var rest = await Rest.StartAsync())
        {
            rest.Mock.Enqueue(response, MockResponse.FromFixture(Kit.Obj("fixtures/requests/search-no-parameters.json")["response"]!));
            var call = rest.Client.Articles.SearchAsync(new SearchParams());
            await Wait.ForAsync(() => rest.Mock.Requests.Count == 1, "the first attempt");
            await Wait.SettleAsync();
            rest.Clock.Advance(TimeSpan.FromMilliseconds(6999));
            await Wait.SettleAsync();
            Assert.Single(rest.Mock.Requests);
            rest.Clock.Advance(TimeSpan.FromMilliseconds(1));
            var page = await call.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(5, page.Articles.Count);
            Assert.Equal(2, rest.Mock.Requests.Count);
            Assert.Single(rest.Mock.Requests.Select(r => r.Header("X-Request-Id")).Distinct());
        }

        // Without Retry-After the wait is BurstWait (10 s); the retry does not count against MaxRetries.
        await using (var rest = await Rest.StartAsync(c => c with { MaxRetries = 0 }))
        {
            var noHeader = response with { Headers = response.Headers.Where(h => h.Key != "retry-after").ToDictionary() };
            rest.Mock.Enqueue(noHeader, noHeader);
            var call = rest.Client.Articles.SearchAsync(new SearchParams());
            await Wait.ForAsync(() => rest.Mock.Requests.Count == 1, "the first attempt");
            await Wait.SettleAsync();
            rest.Clock.Advance(TimeSpan.FromMilliseconds(9999));
            await Wait.SettleAsync();
            Assert.Single(rest.Mock.Requests);
            rest.Clock.Advance(TimeSpan.FromMilliseconds(1));
            var error = await Assert.ThrowsAsync<RateLimitedException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(error.RetryAfter);
            Assert.Equal(2, rest.Mock.Requests.Count);
        }

        // Switched off: thrown at once, Retry-After exposed.
        await using (var rest = await Rest.StartAsync(c => c with { RetryOnBurst = RetryOnBurst.Off }))
        {
            rest.Mock.Enqueue(response);
            var error = await Assert.ThrowsAsync<RateLimitedException>(() => rest.Client.Articles.SearchAsync(new SearchParams()));
            Assert.Equal(TimeSpan.FromSeconds(7), error.RetryAfter);
            Assert.Equal(RateLimitType.Burst, error.LimitType);
            Assert.Single(rest.Mock.Requests);
        }

        // Cancelling during the burst wait ends it at once, as a cancellation.
        await using (var rest = await Rest.StartAsync())
        {
            rest.Mock.Enqueue(response);
            using var cts = new CancellationTokenSource();
            var call = rest.Client.Articles.SearchAsync(new SearchParams(), cts.Token);
            await Wait.ForAsync(() => rest.Mock.Requests.Count == 1, "the first attempt");
            await Wait.SettleAsync();
            await cts.CancelAsync();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => call.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Single(rest.Mock.Requests);
        }
    }
}
