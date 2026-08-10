namespace Finlight;

/// <summary>
/// An enriched news article as returned by the REST API, the enhanced
/// WebSocket stream, and webhooks.
/// </summary>
public sealed class Article
{
    /// <summary>Canonical URL of the article.</summary>
    public required string Link { get; init; }

    /// <summary>Headline.</summary>
    public required string Title { get; init; }

    /// <summary>Publication timestamp.</summary>
    public required DateTimeOffset PublishDate { get; init; }

    /// <summary>Source domain, e.g. "www.reuters.com".</summary>
    public required string Source { get; init; }

    /// <summary>ISO 639-1 language code.</summary>
    public required string Language { get; init; }

    /// <summary>Short summary, when available.</summary>
    public string? Summary { get; init; }

    /// <summary>Image URLs, when available.</summary>
    public IReadOnlyList<string>? Images { get; init; }

    /// <summary>When finlight first stored the article.</summary>
    public DateTimeOffset? CreatedAt { get; init; }

    /// <summary>When the article was last revised by its source.</summary>
    public DateTimeOffset? RevisedDate { get; init; }

    /// <summary>Whether this delivery is an update to a previously sent article.</summary>
    public bool? IsUpdate { get; init; }

    /// <summary>Assigned categories, e.g. "markets" (see <see cref="Category"/> for known values).</summary>
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary>Sentiment label, e.g. "positive".</summary>
    public string? Sentiment { get; init; }

    /// <summary>Confidence of the sentiment classification (0–1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Full article content; only present when requested.</summary>
    public string? Content { get; init; }

    /// <summary>Companies recognized in the article; only present when entities are requested.</summary>
    public IReadOnlyList<Company>? Companies { get; init; }

    /// <summary>ISO 3166-1 alpha-2 codes of countries the article relates to.</summary>
    public IReadOnlyList<string>? Countries { get; init; }
}
