using Finlight.Http;

namespace Finlight;

/// <summary>Fetches financial news articles.</summary>
public sealed class ArticleService
{
    private readonly ApiClient _api;

    internal ArticleService(ApiClient api) => _api = api;

    /// <summary>Searches articles matching <paramref name="parameters"/> and returns one result page.</summary>
    /// <exception cref="FinlightApiException">The server returned a non-2xx response (after retries).</exception>
    public Task<ArticleResponse> FetchArticlesAsync(
        GetArticlesParams parameters,
        CancellationToken cancellationToken = default)
        => _api.SendAsync<ArticleResponse>(HttpMethod.Post, "/v2/articles", null, parameters, cancellationToken);

    /// <summary>Fetches a single article by its URL.</summary>
    /// <exception cref="FinlightApiException">The server returned a non-2xx response (after retries).</exception>
    public async Task<Article> FetchArticleByLinkAsync(
        GetArticleByLinkParams parameters,
        CancellationToken cancellationToken = default)
    {
        var query = "link=" + Uri.EscapeDataString(parameters.Link);
        if (parameters.IncludeContent)
        {
            query += "&includeContent=true";
        }

        if (parameters.IncludeEntities)
        {
            query += "&includeEntities=true";
        }

        var envelope = await _api
            .SendAsync<ArticleEnvelope>(HttpMethod.Get, "/v2/articles/by-link", query, null, cancellationToken)
            .ConfigureAwait(false);
        return envelope.Article;
    }

    private sealed class ArticleEnvelope
    {
        public required Article Article { get; init; }
    }
}
