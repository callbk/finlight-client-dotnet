namespace Finlight;

/// <summary>Filters for the enhanced WebSocket stream (<see cref="WebSockets.ArticleWebSocketClient.StreamAsync"/>).</summary>
public sealed class GetArticlesWebSocketParams
{
    /// <summary>A finlight query-language expression. The stream supports the
    /// <c>ticker:</c>, <c>country:</c>, <c>exchange:</c>, <c>source:</c>,
    /// <c>title:</c>, and <c>summary:</c> fields.</summary>
    public string? Query { get; init; }

    /// <summary>Overrides the default source set.</summary>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>Sources to exclude.</summary>
    public IReadOnlyList<string>? ExcludeSources { get; init; }

    /// <summary>Sources to include on top of the default set.</summary>
    public IReadOnlyList<string>? OptInSources { get; init; }

    /// <summary>ISO 639-1 language code; the server defaults to "en".</summary>
    public string? Language { get; init; }

    /// <summary>Extended payloads.</summary>
    [Obsolete("Use IncludeContent.")]
    public bool? Extended { get; init; }

    /// <summary>Ticker symbols to filter by, e.g. "AAPL".</summary>
    public IReadOnlyList<string>? Tickers { get; init; }

    /// <summary>Include recognized companies in streamed articles.</summary>
    public bool? IncludeEntities { get; init; }

    /// <summary>Skip articles without content.</summary>
    public bool? ExcludeEmptyContent { get; init; }

    /// <summary>Include full article content in streamed articles.</summary>
    public bool? IncludeContent { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country filters.</summary>
    public IReadOnlyList<string>? Countries { get; init; }

    /// <summary>Category filters.</summary>
    public IReadOnlyList<Category>? Categories { get; init; }

    /// <summary>Also stream updates to previously delivered articles.</summary>
    public bool? IncludeUpdates { get; init; }
}
