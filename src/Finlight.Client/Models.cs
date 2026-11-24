using System.Collections.ObjectModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using Finlight.Internal;

namespace Finlight;

/// <summary>
/// One article (ArticleV3), the same object in REST, streams and webhooks. Unknown keys are ignored;
/// <see cref="GetRawJson"/> returns the object as received. Timestamps keep the exact wire string;
/// the <c>...Time</c> properties parse it strictly (null when it is not RFC 3339).
/// </summary>
public sealed record Article : IJsonOnDeserialized
{
    /// <summary><c>id</c>: 22 characters, stable as long as the link is.</summary>
    [JsonPropertyName("id")]
    public required string Id { get; init; }

    /// <summary><c>link</c>: the article URL.</summary>
    [JsonPropertyName("link")]
    public required string Link { get; init; }

    /// <summary><c>source</c>.</summary>
    [JsonPropertyName("source")]
    public required ArticleSource Source { get; init; }

    /// <summary><c>title</c>, in the original language.</summary>
    [JsonPropertyName("title")]
    public required string Title { get; init; }

    /// <summary><c>summary</c>; null when there is none (never <c>""</c> from the server).</summary>
    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    /// <summary><c>language</c>: ISO 639-1 as stored.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary><c>media</c>: at most 3 images; <c>[]</c> when absent or null.</summary>
    [JsonPropertyName("media")]
    [JsonConverter(typeof(NullAsEmptyListConverter<Media>))]
    public IReadOnlyList<Media> Media { get; init; } = [];

    /// <summary><c>publishedAt</c> as sent. Legacy data can hold <c>1970-01-01T00:00:00.000Z</c>.</summary>
    [JsonPropertyName("publishedAt")]
    public string? PublishedAt { get; init; }

    /// <summary><c>firstSeenAt</c> as sent (ingest time).</summary>
    [JsonPropertyName("firstSeenAt")]
    public string? FirstSeenAt { get; init; }

    /// <summary><c>revisedAt</c> as sent.</summary>
    [JsonPropertyName("revisedAt")]
    public string? RevisedAt { get; init; }

    /// <summary><c>availableAt</c> as sent; always null on raw.</summary>
    [JsonPropertyName("availableAt")]
    public string? AvailableAt { get; init; }

    /// <summary><c>countries</c>: the story's focus, ISO alpha-2; <c>[]</c> when absent or null.</summary>
    [JsonPropertyName("countries")]
    [JsonConverter(typeof(NullAsEmptyListConverter<string>))]
    public IReadOnlyList<string> Countries { get; init; } = [];

    /// <summary><c>categories</c>: an open vocabulary; <c>[]</c> when absent or null.</summary>
    [JsonPropertyName("categories")]
    [JsonConverter(typeof(NullAsEmptyListConverter<string>))]
    public IReadOnlyList<string> Categories { get; init; } = [];

    /// <summary><c>content</c>: absent unless requested with <c>include</c> and entitled; null when there is no text.</summary>
    [JsonPropertyName("content")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<string> Content { get; init; }

    /// <summary><c>sentiment</c>: absent without the entitlement; null when not computed.</summary>
    [JsonPropertyName("sentiment")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<Sentiment> Sentiment { get; init; }

    /// <summary><c>companies</c>: absent unless requested and entitled; <c>[]</c> means tagged, no company.</summary>
    [JsonPropertyName("companies")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<IReadOnlyList<Company>> Companies { get; init; }

    /// <summary><c>events</c>: absent unless requested and entitled; <c>[]</c> means classified, no event.</summary>
    [JsonPropertyName("events")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<IReadOnlyList<EventTag>> Events { get; init; }

    /// <summary>True when the key <c>content</c> was in the JSON (also with <c>null</c>).</summary>
    [JsonIgnore]
    public bool ContentPresent => Content.IsPresent;

    /// <summary>True when the key <c>sentiment</c> was in the JSON (also with <c>null</c>).</summary>
    [JsonIgnore]
    public bool SentimentPresent => Sentiment.IsPresent;

    /// <summary>True when the key <c>companies</c> was in the JSON.</summary>
    [JsonIgnore]
    public bool CompaniesPresent => Companies.IsPresent;

    /// <summary>True when the key <c>events</c> was in the JSON.</summary>
    [JsonIgnore]
    public bool EventsPresent => Events.IsPresent;

    /// <summary><see cref="PublishedAt"/> parsed, or null when it is not a strict RFC 3339 time.</summary>
    [JsonIgnore]
    public DateTimeOffset? PublishedAtTime => Rfc3339.Parse(PublishedAt);

    /// <summary><see cref="FirstSeenAt"/> parsed, or null.</summary>
    [JsonIgnore]
    public DateTimeOffset? FirstSeenAtTime => Rfc3339.Parse(FirstSeenAt);

    /// <summary><see cref="RevisedAt"/> parsed, or null.</summary>
    [JsonIgnore]
    public DateTimeOffset? RevisedAtTime => Rfc3339.Parse(RevisedAt);

    /// <summary><see cref="AvailableAt"/> parsed, or null.</summary>
    [JsonIgnore]
    public DateTimeOffset? AvailableAtTime => Rfc3339.Parse(AvailableAt);

    internal string? RawJson { get; set; }

    /// <summary>The article object exactly as received (null for an article built in code).</summary>
    /// <returns>The JSON text, unknown keys included.</returns>
    public string? GetRawJson() => RawJson;

    void IJsonOnDeserialized.OnDeserialized()
    {
        Require.NotNull(Id, "id");
        Require.NotNull(Link, "link");
        Require.NotNull(Title, "title");
        Require.NotNull(Source, "source");
    }
}

/// <summary><c>source</c> of an article.</summary>
public sealed record ArticleSource : IJsonOnDeserialized
{
    /// <summary><c>domain</c>, the join key to <c>/v3/sources</c>.</summary>
    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    /// <summary><c>type</c>: always null today.</summary>
    [JsonPropertyName("type")]
    public SourceType? Type { get; init; }

    void IJsonOnDeserialized.OnDeserialized() => Require.NotNull(Domain, "source.domain");
}

/// <summary>One entry of <c>media</c>.</summary>
public sealed record Media
{
    /// <summary><c>url</c> of the image.</summary>
    [JsonPropertyName("url")]
    public string? Url { get; init; }
}

/// <summary><c>sentiment</c> of an article.</summary>
public sealed record Sentiment
{
    /// <summary><c>label</c>; null when not computed.</summary>
    [JsonPropertyName("label")]
    public SentimentLabel? Label { get; init; }

    /// <summary><c>confidence</c>, full precision.</summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }
}

/// <summary>A company tagged in an article (<c>companies[]</c>).</summary>
public sealed record Company : IJsonOnDeserialized
{
    /// <summary><c>companyId</c>: join key to <c>/v3/companies/{companyId}</c> and <c>events[].companyIds</c>.</summary>
    [JsonPropertyName("companyId")]
    public required long CompanyId { get; init; }

    /// <summary><c>name</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary><c>isin</c>.</summary>
    [JsonPropertyName("isin")]
    public string? Isin { get; init; }

    /// <summary><c>primaryListing</c>; null for a company without a listing (e.g. private).</summary>
    [JsonPropertyName("primaryListing")]
    public Listing? PrimaryListing { get; init; }

    /// <summary><c>confidence</c> of the tag.</summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    /// <summary><c>role</c>: the company's position in the story; null for older tagging.</summary>
    [JsonPropertyName("role")]
    public CompanyRole? Role { get; init; }

    /// <summary><c>relevance</c>, 0 to 1 (aboutness); null for older tagging.</summary>
    [JsonPropertyName("relevance")]
    public double? Relevance { get; init; }

    void IJsonOnDeserialized.OnDeserialized() => Require.NotNull(Name, "name");
}

/// <summary>A listing: <c>primaryListing</c>, or an entry of <see cref="CompanyRecord.Listings"/>.</summary>
public sealed record Listing
{
    /// <summary><c>ticker</c>.</summary>
    [JsonPropertyName("ticker")]
    public string? Ticker { get; init; }

    /// <summary><c>mic</c> (ISO 10383).</summary>
    [JsonPropertyName("mic")]
    public string? Mic { get; init; }

    /// <summary><c>country</c>.</summary>
    [JsonPropertyName("country")]
    public string? Country { get; init; }

    /// <summary><c>active</c>: only set in <see cref="CompanyRecord.Listings"/>.</summary>
    [JsonPropertyName("active")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? Active { get; init; }
}

/// <summary>A classified event of an article (<c>events[]</c>).</summary>
public sealed record EventTag
{
    /// <summary><c>type</c>.</summary>
    [JsonPropertyName("type")]
    public required EventType Type { get; init; }

    /// <summary><c>direction</c>.</summary>
    [JsonPropertyName("direction")]
    public EventDirection? Direction { get; init; }

    /// <summary><c>importance</c>.</summary>
    [JsonPropertyName("importance")]
    public Importance? Importance { get; init; }

    /// <summary><c>confidence</c>.</summary>
    [JsonPropertyName("confidence")]
    public double? Confidence { get; init; }

    /// <summary><c>companyIds</c>: present only when <c>companies</c> is in the same payload.</summary>
    [JsonPropertyName("companyIds")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<IReadOnlyList<long>> CompanyIds { get; init; }
}

/// <summary>Company master data from <c>/v3/companies</c> (CompanyRecordV3).</summary>
public sealed record CompanyRecord : IJsonOnDeserialized
{
    /// <summary><c>companyId</c>.</summary>
    [JsonPropertyName("companyId")]
    public required long CompanyId { get; init; }

    /// <summary><c>name</c>.</summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary><c>country</c> of domicile.</summary>
    [JsonPropertyName("country")]
    public string? Country { get; init; }

    /// <summary><c>sector</c> (GICS sector name).</summary>
    [JsonPropertyName("sector")]
    public string? Sector { get; init; }

    /// <summary><c>industry</c>.</summary>
    [JsonPropertyName("industry")]
    public string? Industry { get; init; }

    /// <summary><c>isins</c>; <c>[]</c> when absent or null.</summary>
    [JsonPropertyName("isins")]
    [JsonConverter(typeof(NullAsEmptyListConverter<string>))]
    public IReadOnlyList<string> Isins { get; init; } = [];

    /// <summary><c>primaryListing</c>.</summary>
    [JsonPropertyName("primaryListing")]
    public Listing? PrimaryListing { get; init; }

    /// <summary><c>listings</c>: one per exchange, the primary first; <c>[]</c> when absent or null.</summary>
    [JsonPropertyName("listings")]
    [JsonConverter(typeof(NullAsEmptyListConverter<Listing>))]
    public IReadOnlyList<Listing> Listings { get; init; } = [];

    /// <summary><c>updatedAt</c> as sent.</summary>
    [JsonPropertyName("updatedAt")]
    public string? UpdatedAt { get; init; }

    /// <summary><see cref="UpdatedAt"/> parsed, or null.</summary>
    [JsonIgnore]
    public DateTimeOffset? UpdatedAtTime => Rfc3339.Parse(UpdatedAt);

    internal string? RawJson { get; set; }

    internal CacheInfo? Cache { get; set; }

    /// <summary>The record exactly as received.</summary>
    /// <returns>The JSON text, or null for a record built in code.</returns>
    public string? GetRawJson() => RawJson;

    /// <summary>ETag and Cache-Control of a record from <c>companies.get</c>; null otherwise.</summary>
    /// <returns>The cache data or null.</returns>
    public CacheInfo? GetCacheInfo() => Cache;

    void IJsonOnDeserialized.OnDeserialized() => Require.NotNull(Name, "name");
}

/// <summary>HTTP caching data of a record from <c>companies.get</c>.</summary>
/// <param name="Etag">The <c>ETag</c> header; pass it as <see cref="CompanyGetOptions.IfNoneMatch"/>.</param>
/// <param name="CacheControl">The <c>Cache-Control</c> header.</param>
/// <param name="NotModified">True when the server answered 304 and the SDK returned its cached copy.</param>
public sealed record CacheInfo(string? Etag, string? CacheControl, bool NotModified);

/// <summary>One source of <c>/v3/sources</c> (SourceV3).</summary>
public sealed record Source : IJsonOnDeserialized
{
    /// <summary><c>domain</c>.</summary>
    [JsonPropertyName("domain")]
    public required string Domain { get; init; }

    /// <summary><c>name</c>: null until the catalog is curated.</summary>
    [JsonPropertyName("name")]
    public string? Name { get; init; }

    /// <summary><c>type</c>: null today.</summary>
    [JsonPropertyName("type")]
    public SourceType? Type { get; init; }

    /// <summary><c>isDefault</c>: part of the default pool.</summary>
    [JsonPropertyName("isDefault")]
    public bool? IsDefault { get; init; }

    /// <summary><c>hasContent</c>: absent without <c>fullArticleAccess</c> (unknown, not false).</summary>
    [JsonPropertyName("hasContent")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public Optional<bool> HasContent { get; init; }

    /// <summary><c>isCustom</c>: a custom source granted to this subscription.</summary>
    [JsonPropertyName("isCustom")]
    public bool? IsCustom { get; init; }

    /// <summary><c>country</c>.</summary>
    [JsonPropertyName("country")]
    public string? Country { get; init; }

    /// <summary><c>languages</c>, the most common first; <c>[]</c> when absent or null.</summary>
    [JsonPropertyName("languages")]
    [JsonConverter(typeof(NullAsEmptyListConverter<string>))]
    public IReadOnlyList<string> Languages { get; init; } = [];

    internal string? RawJson { get; set; }

    /// <summary>The source exactly as received.</summary>
    /// <returns>The JSON text, or null for a source built in code.</returns>
    public string? GetRawJson() => RawJson;

    void IJsonOnDeserialized.OnDeserialized() => Require.NotNull(Domain, "domain");
}

/// <summary><c>pagination</c> of a search page.</summary>
public sealed record Pagination
{
    /// <summary><c>limit</c>: the page size used.</summary>
    [JsonPropertyName("limit")]
    public int Limit { get; init; }

    /// <summary><c>returned</c>: articles on this page.</summary>
    [JsonPropertyName("returned")]
    public int Returned { get; init; }

    /// <summary><c>hasMore</c>.</summary>
    [JsonPropertyName("hasMore")]
    public bool HasMore { get; init; }

    /// <summary><c>nextCursor</c>: opaque; pass it as <see cref="SearchParams.Cursor"/>. Null at the end.</summary>
    [JsonPropertyName("nextCursor")]
    public string? NextCursor { get; init; }
}

/// <summary>An entry of a list that could not be decoded; it was left out and logged.</summary>
/// <param name="Index">Position in the list as received.</param>
/// <param name="Raw">The entry as JSON text.</param>
/// <param name="Reason">Why it could not be decoded.</param>
public sealed record SkippedEntry(int Index, string Raw, string Reason);

/// <summary>One page of <c>articles.search</c>.</summary>
public sealed record ArticlePage
{
    /// <summary>The articles that decoded.</summary>
    public required IReadOnlyList<Article> Articles { get; init; }

    /// <summary><c>pagination</c>.</summary>
    public required Pagination Pagination { get; init; }

    /// <summary>Entries that could not be decoded (also logged as warnings).</summary>
    public IReadOnlyList<SkippedEntry> Skipped { get; init; } = [];
}

/// <summary>The result of <c>companies.search</c> and <c>companies.getMany</c>.</summary>
public sealed class CompanyList : ReadOnlyCollection<CompanyRecord>
{
    internal CompanyList(IList<CompanyRecord> items, IReadOnlyList<SkippedEntry> skipped)
        : base(items)
    {
        Skipped = skipped;
    }

    /// <summary>Entries that could not be decoded (also logged as warnings).</summary>
    public IReadOnlyList<SkippedEntry> Skipped { get; }
}

/// <summary>The result of <c>sources.list</c>.</summary>
public sealed class SourceList : ReadOnlyCollection<Source>
{
    internal SourceList(IList<Source> items, IReadOnlyList<SkippedEntry> skipped)
        : base(items)
    {
        Skipped = skipped;
    }

    /// <summary>Entries that could not be decoded (also logged as warnings).</summary>
    public IReadOnlyList<SkippedEntry> Skipped { get; }
}

/// <summary>Required-key checks after deserialization (R-MODELS-1).</summary>
internal static class Require
{
    public static void NotNull(object? value, string key)
    {
        if (value is null)
        {
            throw new JsonException($"required key '{key}' is null");
        }
    }
}
