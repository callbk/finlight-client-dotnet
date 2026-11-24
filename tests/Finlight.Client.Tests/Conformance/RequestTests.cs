using System.Text.Json;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Conformance;

/// <summary>R-01 to R-12: the HTTP request the SDK puts on the wire, and the decoded answer.</summary>
public sealed class RequestTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["R-01"] = "search-minimal.json",
        ["R-02"] = "search-all-parameters.json",
        ["R-03"] = "search-no-parameters.json",
        ["R-04"] = "get-by-id.json",
        ["R-05"] = "get-by-link.json",
        ["R-06"] = "companies-search.json",
        ["R-07"] = "companies-get-many.json",
        ["R-08"] = "companies-get.json",
        ["R-09"] = "companies-get-not-modified.json",
        ["R-10"] = "sources-list-full.json",
        ["R-11"] = "sources-list-basic.json",
        ["R-12"] = "search-unknown-key.json",
    };

    public static TheoryData<string> Cases => [.. Files.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string id)
    {
        var fixture = Kit.Obj("fixtures/requests/" + Files[id]);
        Assert.Equal(id, fixture["id"]!.GetValue<string>());
        var expectedKey = fixture["request"]!["headers"]!["X-API-KEY"]!.GetValue<string>();
        await using var rest = await Rest.StartAsync(apiKey: expectedKey);
        var args = fixture["sdk"]!["args"];

        if (id == "R-12")
        {
            // The parameter type has no such key: a typo does not compile, and mapping the kit's
            // arguments fails before any request (R-REQ-3).
            Assert.Throws<JsonException>(() => Rest.Args<SearchParams>(args));
            // No member of any kind, in any spelling, maps to the kit's misspelled key.
            Assert.DoesNotContain(
                typeof(SearchParams).GetMembers(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance),
                m => string.Equals(m.Name, "EventType", StringComparison.OrdinalIgnoreCase));
            Assert.Empty(rest.Mock.Requests);
            return;
        }

        if (id == "R-09")
        {
            await NotModifiedAsync(rest, fixture);
            return;
        }

        rest.Mock.Enqueue(MockResponse.FromFixture(fixture["response"]!));
        var method = fixture["sdk"]!["method"]!.GetValue<string>();
        object result = method switch
        {
            "articles.search" => await rest.Client.Articles.SearchAsync(Rest.Args<SearchParams>(args)),
            "articles.get" => await rest.Client.Articles.GetAsync(args!["id"]!.GetValue<string>(), Lookup(args)),
            "articles.getByLink" => await rest.Client.Articles.GetByLinkAsync(args!["link"]!.GetValue<string>(), Lookup(args)),
            "companies.search" => await rest.Client.Companies.SearchAsync(Rest.Args<CompanySearchParams>(args)),
            "companies.getMany" => await rest.Client.Companies.GetManyAsync(args!["ids"]!.AsArray().Select(i => i!.GetValue<long>())),
            "companies.get" => (await rest.Client.Companies.GetAsync(args!["companyId"]!.GetValue<long>()))!,
            "sources.list" => await rest.Client.Sources.ListAsync(),
            _ => throw new InvalidOperationException(method),
        };

        var request = Assert.Single(rest.Mock.Requests);
        AssertRequest(fixture["request"]!, request);
        var body = fixture["response"]!["body"]!;
        switch (result)
        {
            case ArticlePage page:
                Assert.Equal(body["articles"]!.AsArray().Count, page.Articles.Count);
                Assert.Equal(body["pagination"]!["hasMore"]!.GetValue<bool>(), page.Pagination.HasMore);
                Assert.Equal(body["pagination"]!["limit"]!.GetValue<int>(), page.Pagination.Limit);
                Assert.Empty(page.Skipped);
                break;
            case Article article:
                Assert.Equal(body["article"]!["id"]!.GetValue<string>(), article.Id);
                Assert.Equal(Kit.Canonical(body["article"]), Kit.Canonical(article.GetRawJson()!));
                break;
            case CompanyList companies:
                Assert.Equal(body["companies"]!.AsArray().Count, companies.Count);
                Assert.Empty(companies.Skipped);
                if (id == "R-07")
                {
                    var privateCompany = companies.Single(c => c.PrimaryListing is null);
                    Assert.Empty(privateCompany.Listings);
                }

                break;
            case CompanyRecord record:
                Assert.Equal(900001, record.CompanyId);
                var cache = record.GetCacheInfo()!;
                Assert.Equal(fixture["response"]!["headers"]!["etag"]!.GetValue<string>(), cache.Etag);
                Assert.Equal(fixture["response"]!["headers"]!["cache-control"]!.GetValue<string>(), cache.CacheControl);
                // No caller ifNoneMatch: no If-None-Match, the SDK keeps no company cache of its own.
                Assert.False(Assert.Single(rest.Mock.Requests).Headers.ContainsKey("If-None-Match"));
                Assert.Equal(record.Listings.Count, record.Listings.Select(l => l.Mic).Distinct().Count());
                Assert.Equal(record.PrimaryListing!.Mic, record.Listings[0].Mic);
                Assert.True(record.Listings[0].Active);
                break;
            case SourceList sources:
                Assert.Equal(body["sources"]!.AsArray().Count, sources.Count);
                if (id == "R-10")
                {
                    Assert.All(sources, s => Assert.True(s.HasContent.IsPresent));
                    Assert.Contains(sources, s => s.IsCustom == true);
                    Assert.Contains(sources, s => s.Type == SourceType.Blog);
                    Assert.Contains(sources, s => s.Type == SourceType.FinancialMedia);
                }
                else
                {
                    Assert.All(sources, s => Assert.False(s.HasContent.IsPresent));
                    Assert.All(sources, s => Assert.NotEqual(true, s.IsCustom));
                }

                Assert.Equal(Kit.Canonical(body["sources"]![0]), Kit.Canonical(Presence.Roundtrip(sources[0])));
                break;
        }
    }

    private static async Task NotModifiedAsync(Rest rest, JsonObject fixture)
    {
        var args = fixture["sdk"]!["args"]!;
        var etag = args["ifNoneMatch"]!.GetValue<string>();
        rest.Mock.Enqueue(MockResponse.FromFixture(fixture["response"]!));
        var result = await rest.Client.Companies.GetAsync(args["companyId"]!.GetValue<long>(), new CompanyGetOptions { IfNoneMatch = etag });
        Assert.Null(result);
        AssertRequest(fixture["request"]!, Assert.Single(rest.Mock.Requests));

        // The SDK keeps no cache: the same id again without ifNoneMatch is a plain request, never revalidated.
        var full = Kit.Obj("fixtures/requests/companies-get.json");
        rest.Mock.Enqueue(MockResponse.FromFixture(full["response"]!), MockResponse.FromFixture(full["response"]!));
        Assert.NotNull(await rest.Client.Companies.GetAsync(900001));
        Assert.NotNull(await rest.Client.Companies.GetAsync(900001));
        var requests = rest.Mock.Requests;
        Assert.Equal(3, requests.Count);
        Assert.False(requests[1].Headers.ContainsKey("If-None-Match"));
        Assert.False(requests[2].Headers.ContainsKey("If-None-Match"));
    }

    private static LookupOptions? Lookup(JsonNode? args)
        => args?["include"] is JsonArray include
            ? new LookupOptions { Include = [.. include.Select(i => new IncludeBlock(i!.GetValue<string>()))] }
            : null;

    internal static void AssertRequest(JsonNode expected, RecordedRequest actual)
    {
        Assert.Equal(expected["method"]!.GetValue<string>(), actual.Method);
        Assert.Equal(expected["path"]!.GetValue<string>(), actual.Path);
        var expectedQuery = Query(expected["query"]?.GetValue<string>() ?? string.Empty);
        Assert.Equal(expectedQuery, actual.Query.OrderBy(p => p.Key).ToList());
        Assert.DoesNotContain("[]", actual.RawQuery, StringComparison.Ordinal);
        Assert.Equal(actual.Query.Count, actual.Query.Select(p => p.Key).Distinct().Count());
        foreach (var (name, value) in expected["headers"]!.AsObject())
        {
            Assert.Equal(value!.GetValue<string>(), actual.Header(name));
        }

        Assert.StartsWith("req_", actual.Header("X-Request-Id"), StringComparison.Ordinal);
        Assert.Equal($"finlight-client-dotnet/{FinlightClient.Version}", actual.Header("User-Agent"));
        Assert.Equal(FinlightClient.Version, actual.Header("x-client-version"));
        if (expected["body"] is { } body)
        {
            Assert.StartsWith("application/json", actual.Header("Content-Type"), StringComparison.Ordinal);
            Assert.Equal(Kit.Canonical(body), Kit.Canonical(actual.BodyText));
        }
        else
        {
            Assert.Empty(actual.Body);
        }
    }

    private static List<KeyValuePair<string, string>> Query(string raw)
        => [.. raw.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2))
            .Select(p => new KeyValuePair<string, string>(Uri.UnescapeDataString(p[0]), Uri.UnescapeDataString(p.Length > 1 ? p[1] : string.Empty)))
            .OrderBy(p => p.Key)];
}
