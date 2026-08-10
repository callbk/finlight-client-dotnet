using System.Net;
using Finlight.Tests.Support;

namespace Finlight.Tests;

public class ArticleServiceTests
{
    private static (FinlightClient Client, FakeHttpMessageHandler Handler) CreateClient()
    {
        var handler = new FakeHttpMessageHandler();
        var options = new FinlightClientOptions { ApiKey = "test-key" };
        var client = new FinlightClient(options, new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan });
        return (client, handler);
    }

    [Fact]
    public async Task FetchArticles_ParsesPage()
    {
        var (client, handler) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """
            {
                "status": "ok",
                "page": 2,
                "pageSize": 1,
                "articles": [{
                    "link": "https://example.com/a",
                    "title": "Title",
                    "publishDate": "2024-01-01T00:00:00Z",
                    "source": "example.com",
                    "language": "en",
                    "sentiment": "positive",
                    "confidence": "0.95"
                }]
            }
            """);

        var response = await client.Articles.FetchArticlesAsync(new GetArticlesParams { Page = 2, PageSize = 1 });

        Assert.Equal(2, response.Page);
        var article = Assert.Single(response.Articles);
        Assert.Equal("https://example.com/a", article.Link);
        Assert.Equal(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero), article.PublishDate);
        Assert.Equal(0.95, article.Confidence);
    }

    [Fact]
    public async Task FetchArticleByLink_UnwrapsEnvelopeAndEncodesQuery()
    {
        var (client, handler) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """
            {
                "article": {
                    "link": "https://example.com/a?id=1",
                    "title": "Title",
                    "publishDate": "2024-01-01T00:00:00Z",
                    "source": "example.com",
                    "language": "en",
                    "content": "Full content"
                }
            }
            """);

        var article = await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams
        {
            Link = "https://example.com/a?id=1",
            IncludeContent = true,
        });

        Assert.Equal("Full content", article.Content);
        var request = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Get, request.Method);
        Assert.Equal("/v2/articles/by-link", request.Uri?.AbsolutePath);
        Assert.Equal("?link=https%3A%2F%2Fexample.com%2Fa%3Fid%3D1&includeContent=true", request.Uri?.Query);
        Assert.Null(request.Body);
    }

    [Fact]
    public async Task FetchArticleByLink_OmitsUnsetFlags()
    {
        var (client, handler) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """
            {"article": {"link": "https://example.com/a", "title": "T", "publishDate": "2024-01-01", "source": "s", "language": "en"}}
            """);

        await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams { Link = "https://example.com/a" });

        var request = Assert.Single(handler.Requests);
        Assert.DoesNotContain("includeContent", request.Uri?.Query);
        Assert.DoesNotContain("includeEntities", request.Uri?.Query);
    }

    [Fact]
    public async Task GetSources_ParsesBareArrayIncludingNewerFields()
    {
        var (client, handler) = CreateClient();
        handler.Enqueue(HttpStatusCode.OK, """
            [
                {"domain": "example.com", "isContentAvailable": true, "isDefaultSource": false,
                 "originCountry": "US", "languages": ["en", "de"], "isCustomSource": false},
                {"domain": "legacy.com", "isContentAvailable": false, "isDefaultSource": true}
            ]
            """);

        var sources = await client.Sources.GetSourcesAsync();

        Assert.Equal(2, sources.Count);
        Assert.Equal("example.com", sources[0].Domain);
        Assert.True(sources[0].IsContentAvailable);
        Assert.Equal("US", sources[0].OriginCountry);
        Assert.Equal(["en", "de"], sources[0].Languages);
        Assert.False(sources[0].IsCustomSource);
        Assert.Null(sources[1].OriginCountry);
        var request = Assert.Single(handler.Requests);
        Assert.Equal("/v2/sources", request.Uri?.AbsolutePath);
    }
}
