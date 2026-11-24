using System.Text.Json.Nodes;
using Finlight.Tests.Support;

namespace Finlight.Tests.Conformance;

/// <summary>D-01 to D-11: decode fixtures, presence of every key, write-back.</summary>
public sealed class DecodeTests
{
    private static readonly Dictionary<string, string> Files = new()
    {
        ["D-01"] = "full-article.json",
        ["D-02"] = "raw-skeleton.json",
        ["D-03"] = "no-include-full-access.json",
        ["D-04"] = "all-include-no-access.json",
        ["D-05"] = "events-without-companies.json",
        ["D-06"] = "events-companies-not-entitled.json",
        ["D-07"] = "legacy-values.json",
        ["D-08"] = "non-ascii.json",
        ["D-09"] = "partial-values.json",
        ["D-10"] = "matrix.json",
        ["D-11"] = "blank-text.json",
    };

    public static TheoryData<string> Cases => [.. Files.Keys];

    [Theory]
    [MemberData(nameof(Cases))]
    public void Case(string id)
    {
        var fixture = Kit.Obj("fixtures/decode/" + Files[id]);
        Assert.Equal(id, fixture["id"]!.GetValue<string>());
        if (id == "D-10")
        {
            var cases = fixture["cases"]!.AsArray();
            Assert.Equal(fixture["count"]!.GetValue<int>(), cases.Count);
            foreach (var sub in cases)
            {
                Check(sub!.AsObject());
            }

            return;
        }

        var article = Check(fixture);
        var wire = fixture["wire"]!;
        switch (id)
        {
            case "D-01":
                Assert.Equal(3, article.Media.Count);
                Assert.Equal("2026-09-18T14:30:00.000Z", article.RevisedAt);
                Assert.Null(article.Companies.Value![1].PrimaryListing);
                Assert.Equal(new DateTimeOffset(2026, 9, 18, 12, 3, 5, 123, TimeSpan.Zero), article.AvailableAtTime);
                Assert.Equal(0.9812, article.Sentiment.Value!.Confidence);
                Assert.Equal([900001L, 900002L], article.Events.Value![0].CompanyIds.Value!);
                break;
            case "D-02":
                Assert.Null(article.AvailableAt);
                Assert.False(article.ContentPresent || article.SentimentPresent || article.CompaniesPresent || article.EventsPresent);
                break;
            case "D-04":
                Assert.False(article.Content.IsPresent);
                Assert.Null(article.Content.Value);
                Assert.False(article.Companies.IsPresent);
                break;
            case "D-05":
            case "D-06":
                Assert.True(article.EventsPresent);
                Assert.False(article.CompaniesPresent);
                Assert.All(article.Events.Value!, e => Assert.False(e.CompanyIds.IsPresent));
                break;
            case "D-07":
                Assert.Equal("1970-01-01T00:00:00.000Z", article.PublishedAt);
                Assert.Equal(DateTimeOffset.UnixEpoch, article.PublishedAtTime);
                Assert.Null(article.Summary);
                Assert.Null(article.FirstSeenAt);
                Assert.Null(article.FirstSeenAtTime);
                Assert.True(article.SentimentPresent);
                Assert.Null(article.Sentiment.Value);
                Assert.Null(article.Companies.Value![0].Role);
                Assert.Null(article.Companies.Value![0].Relevance);
                Assert.Empty(article.Events.Value!);
                Assert.True(article.ContentPresent);
                Assert.Null(article.Content.Value);
                Assert.Empty(article.Countries);
                Assert.Empty(article.Categories);
                break;
            case "D-08":
                Assert.Equal(wire["title"]!.GetValue<string>(), article.Title);
                Assert.Equal(wire["summary"]!.GetValue<string>(), article.Summary);
                break;
            case "D-09":
                Assert.Null(article.Sentiment.Value!.Label);
                Assert.NotNull(article.Sentiment.Value!.Confidence);
                Assert.True(article.CompaniesPresent);
                Assert.Empty(article.Companies.Value!);
                Assert.Null(article.Events.Value![0].Direction);
                Assert.Null(article.Events.Value![0].Importance);
                Assert.Null(article.Events.Value![0].Confidence);
                break;
            case "D-11":
                Assert.Null(article.Summary);
                Assert.True(article.ContentPresent);
                Assert.Null(article.Content.Value);
                Assert.Equal(wire["title"]!.GetValue<string>(), article.Title);
                break;
        }
    }

    /// <summary>Decodes the wire, checks every presence entry, and checks the write-back keeps absent keys absent.</summary>
    private static Article Check(JsonObject fixture)
    {
        var wire = fixture["wire"]!;
        var article = Presence.Article(wire);
        foreach (var (path, state) in fixture["expect"]!["presence"]!.AsObject())
        {
            Assert.True(
                state!.GetValue<string>() == Presence.Of(article, path),
                $"{fixture["id"]}: {path} should be {state} but is {Presence.Of(article, path)}");
        }

        Assert.Equal(Kit.Canonical(wire), Kit.Canonical(Presence.Roundtrip(article)));
        Assert.Equal(Kit.Canonical(wire), Kit.Canonical(article.GetRawJson()!));
        return article;
    }
}
