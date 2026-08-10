using System.Text.Json.Serialization;

namespace Finlight;

/// <summary>
/// Search parameters for <see cref="ArticleService.FetchArticlesAsync"/>.
/// Unset fields are omitted from the request; the server applies its
/// documented defaults.
/// </summary>
public sealed class GetArticlesParams
{
    /// <summary>
    /// A finlight query-language expression, e.g.
    /// <c>(ticker:AAPL OR ticker:NVDA) AND NOT source:www.reuters.com AND "Elon Musk"</c>.
    /// </summary>
    public string? Query { get; init; }

    /// <summary>Single source filter.</summary>
    [Obsolete("Use Sources.")]
    public string? Source { get; init; }

    /// <summary>Overrides the default source set.</summary>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>Sources to exclude.</summary>
    public IReadOnlyList<string>? ExcludeSources { get; init; }

    /// <summary>Sources to include on top of the default set.</summary>
    public IReadOnlyList<string>? OptInSources { get; init; }

    /// <summary>Start date, "YYYY-MM-DD" or ISO 8601.</summary>
    [JsonPropertyName("from")]
    public string? From { get; init; }

    /// <summary>End date, "YYYY-MM-DD" or ISO 8601.</summary>
    public string? To { get; init; }

    /// <summary>ISO 639-1 language code; the server defaults to "en".</summary>
    public string? Language { get; init; }

    /// <summary>Ticker symbols to filter by, e.g. "AAPL".</summary>
    public IReadOnlyList<string>? Tickers { get; init; }

    /// <summary>Include recognized companies in the response.</summary>
    public bool? IncludeEntities { get; init; }

    /// <summary>Skip articles without content.</summary>
    public bool? ExcludeEmptyContent { get; init; }

    /// <summary>Include full article content in the response.</summary>
    public bool? IncludeContent { get; init; }

    /// <summary>Sort field.</summary>
    public ArticleOrderBy? OrderBy { get; init; }

    /// <summary>Sort direction.</summary>
    public SortOrder? Order { get; init; }

    /// <summary>Results per page (1–1000).</summary>
    public int? PageSize { get; init; }

    /// <summary>Page number (1-based).</summary>
    public int? Page { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country filters.</summary>
    public IReadOnlyList<string>? Countries { get; init; }

    /// <summary>Category filters.</summary>
    public IReadOnlyList<Category>? Categories { get; init; }
}
