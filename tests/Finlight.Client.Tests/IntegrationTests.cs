using Finlight.Tests.Support;

namespace Finlight.Tests;

/// <summary>
/// Smoke tests against the live API. Run with:
/// <c>FINLIGHT_API_KEY=... dotnet test</c>. Optional overrides:
/// FINLIGHT_BASE_URL, FINLIGHT_WSS_URL.
/// </summary>
public class IntegrationTests
{
    private static FinlightClient CreateClient()
        => new(new FinlightClientOptions
        {
            ApiKey = Environment.GetEnvironmentVariable("FINLIGHT_API_KEY")!,
            BaseUrl = Environment.GetEnvironmentVariable("FINLIGHT_BASE_URL") ?? "https://api.finlight.me",
            WssUrl = Environment.GetEnvironmentVariable("FINLIGHT_WSS_URL") ?? "wss://wss.finlight.me",
        });

    [IntegrationFact]
    public async Task FetchArticles_ReturnsArticles()
    {
        using var client = CreateClient();

        var response = await client.Articles.FetchArticlesAsync(new GetArticlesParams { PageSize = 3 });

        Assert.NotEmpty(response.Articles);
        Assert.All(response.Articles, article => Assert.False(string.IsNullOrEmpty(article.Link)));
    }

    [IntegrationFact]
    public async Task FetchArticleByLink_ReturnsSearchedArticle()
    {
        using var client = CreateClient();
        var page = await client.Articles.FetchArticlesAsync(new GetArticlesParams { PageSize = 1 });
        var link = Assert.Single(page.Articles).Link;

        var article = await client.Articles.FetchArticleByLinkAsync(new GetArticleByLinkParams { Link = link });

        Assert.Equal(link, article.Link);
    }

    [IntegrationFact]
    public async Task GetSources_ReturnsSources()
    {
        using var client = CreateClient();

        var sources = await client.Sources.GetSourcesAsync();

        Assert.NotEmpty(sources);
        Assert.All(sources, source => Assert.False(string.IsNullOrEmpty(source.Domain)));
    }

    [IntegrationFact]
    public async Task WebSocket_ConnectsAndStreams()
    {
        using var client = CreateClient();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        // Passing means the lowercase-header handshake was accepted and the
        // stream stayed healthy for the window; quiet news periods yield no
        // articles, which is fine.
        try
        {
            await foreach (var article in client.WebSocket.StreamAsync(new GetArticlesWebSocketParams(), cts.Token))
            {
                Assert.False(string.IsNullOrEmpty(article.Link));
                break;
            }
        }
        catch (OperationCanceledException)
        {
        }
    }
}
