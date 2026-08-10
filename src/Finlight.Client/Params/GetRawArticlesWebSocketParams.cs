namespace Finlight;

/// <summary>Filters for the raw WebSocket stream (<see cref="WebSockets.RawArticleWebSocketClient.StreamAsync"/>).</summary>
public sealed class GetRawArticlesWebSocketParams
{
    /// <summary>A finlight query-language expression. The raw stream supports
    /// the <c>source:</c>, <c>title:</c>, and <c>summary:</c> fields.</summary>
    public string? Query { get; init; }

    /// <summary>Overrides the default source set.</summary>
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary>Sources to exclude.</summary>
    public IReadOnlyList<string>? ExcludeSources { get; init; }

    /// <summary>Sources to include on top of the default set.</summary>
    public IReadOnlyList<string>? OptInSources { get; init; }

    /// <summary>ISO 639-1 language code; the server defaults to "en".</summary>
    public string? Language { get; init; }

    /// <summary>Also stream updates to previously delivered articles.</summary>
    public bool? IncludeUpdates { get; init; }
}
