namespace Finlight;

/// <summary>Parameters for <see cref="ArticleService.FetchArticleByLinkAsync"/>.</summary>
public sealed class GetArticleByLinkParams
{
    /// <summary>URL of the article to fetch.</summary>
    public required string Link { get; init; }

    /// <summary>Include full article content in the response.</summary>
    public bool IncludeContent { get; init; }

    /// <summary>Include recognized companies in the response.</summary>
    public bool IncludeEntities { get; init; }
}
