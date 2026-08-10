namespace Finlight;

/// <summary>
/// One result page of <see cref="ArticleService.FetchArticlesAsync"/>. The API
/// reports no total count; advance <see cref="GetArticlesParams.Page"/> until a
/// short or empty page comes back.
/// </summary>
public sealed class ArticleResponse
{
    /// <summary>Request status as reported by the server.</summary>
    public required string Status { get; init; }

    /// <summary>The returned page number (1-based).</summary>
    public required int Page { get; init; }

    /// <summary>The page size used for this response.</summary>
    public required int PageSize { get; init; }

    /// <summary>The articles on this page.</summary>
    public required IReadOnlyList<Article> Articles { get; init; }
}
