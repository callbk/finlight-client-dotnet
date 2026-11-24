using System.Text.Json;
using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Conformance;

/// <summary>P-01 to P-06: the cursor walk and what the server says about changed bodies.</summary>
public sealed class PaginationTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["P-01"] = "three-pages.json",
        ["P-02"] = "resize-and-reproject.json",
        ["P-03"] = "injected-to.json",
        ["P-04"] = "injected-default-languages.json",
        ["P-05"] = "changed-order.json",
        ["P-06"] = "reordered-list.json",
    };

    public static TheoryData<string> Cases => [.. Files.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task Case(string id)
    {
        var fixture = Kit.Obj("fixtures/pagination/" + Files[id]);
        Assert.Equal(id, fixture["id"]!.GetValue<string>());
        await using var rest = await Rest.StartAsync();
        if (id == "P-01")
        {
            await ThreePagesAsync(rest, fixture);
            return;
        }

        // One page sent with exactly the fixture body (cursor included); the outcome is the server's.
        rest.Mock.Enqueue(MockResponse.FromFixture(fixture["response"]!));
        var parameters = Rest.Args<SearchParams>(fixture["request"]!["body"]);
        var status = fixture["response"]!["status"]!.GetValue<int>();
        if (status == 200)
        {
            var page = await rest.Client.Articles.SearchAsync(parameters);
            Assert.Equal(fixture["response"]!["body"]!["articles"]!.AsArray().Count, page.Articles.Count);
        }
        else
        {
            var error = await Assert.ThrowsAsync<CursorMismatchException>(() => rest.Client.Articles.SearchAsync(parameters));
            Assert.Equal("CURSOR_MISMATCH", error.Code);
            Assert.Equal("cursor", error.Errors[0].Field);
            if (id == "P-05")
            {
                Assert.Contains("publishedAt:desc", error.Detail, StringComparison.Ordinal);
                Assert.Contains("publishedAt:asc", error.Detail, StringComparison.Ordinal);
            }
        }

        var request = Assert.Single(rest.Mock.Requests);
        Assert.Equal(Kit.Canonical(fixture["request"]!["body"]), Kit.Canonical(request.BodyText));
    }

    private static async Task ThreePagesAsync(Rest rest, JsonObject fixture)
    {
        var steps = fixture["steps"]!.AsArray();
        foreach (var step in steps)
        {
            rest.Mock.Enqueue(MockResponse.FromFixture(step!["response"]!));
        }

        // The caller's list changes after the call: the frozen body must not.
        var sources = fixture["sdk"]!["args"]!["sources"]!.AsArray().Select(s => s!.GetValue<string>()).ToList();
        var parameters = new SearchParams { Sources = sources, Limit = fixture["sdk"]!["args"]!["limit"]!.GetValue<int>() };
        var result = rest.Client.Articles.SearchAllAsync(parameters);
        sources.Add("late.example.org");

        var articles = new List<Article>();
        await foreach (var article in result)
        {
            articles.Add(article);
        }

        var requests = rest.Mock.Requests;
        Assert.Equal(3, requests.Count);
        Assert.Equal(3, result.PagesFetched);
        Assert.False(result.Truncated);
        Assert.Null(result.NextCursor);
        var expectedIds = steps.SelectMany(s => s!["response"]!["body"]!["articles"]!.AsArray().Select(a => a!["id"]!.GetValue<string>())).ToList();
        Assert.Equal(5, expectedIds.Count);
        Assert.Equal(expectedIds, articles.Select(a => a.Id));
        for (var i = 0; i < 3; i++)
        {
            RequestTests.AssertRequest(steps[i]!["request"]!, requests[i]);
        }

        // Pages 2 and 3 are page 1 byte for byte, plus the cursor of the previous page.
        var first = requests[0].BodyText;
        for (var i = 1; i < 3; i++)
        {
            var cursor = steps[i - 1]!["response"]!["body"]!["pagination"]!["nextCursor"]!.GetValue<string>();
            Assert.Equal(first[..^1] + ",\"cursor\":" + JsonSerializer.Serialize(cursor) + "}", requests[i].BodyText);
        }

        // One X-Request-Id per page.
        Assert.Equal(3, requests.Select(r => r.Header("X-Request-Id")).Distinct().Count());

        // A second iteration throws instead of searching again.
        Assert.Throws<FinlightValidationException>(() => result.GetAsyncEnumerator());
        Assert.Throws<FinlightValidationException>(() => result.Pages());
    }
}
