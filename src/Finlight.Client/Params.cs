using System.Globalization;
using System.Text.Json.Serialization;
using Finlight.Internal;

namespace Finlight;

/// <summary>
/// A date bound for <see cref="SearchParams.From"/> and <see cref="SearchParams.To"/>. A
/// <see cref="DateTimeOffset"/> or a UTC/local <see cref="DateTime"/> goes out as UTC ISO 8601 with
/// milliseconds and <c>Z</c>; a <see cref="DateOnly"/> as <c>YYYY-MM-DD</c>; a string unchanged.
/// A <see cref="DateTime"/> of kind <see cref="DateTimeKind.Unspecified"/> is rejected before sending.
/// </summary>
[JsonConverter(typeof(DateInputConverter))]
public readonly struct DateInput : IEquatable<DateInput>
{
    private readonly string? _text;
    private readonly DateTimeOffset? _instant;
    private readonly DateOnly? _date;
    private readonly bool _unspecified;

    private DateInput(string? text, DateTimeOffset? instant, DateOnly? date, bool unspecified)
    {
        _text = text;
        _instant = instant;
        _date = date;
        _unspecified = unspecified;
    }

    /// <summary>A string, sent exactly as given (e.g. <c>2026-11-01</c>).</summary>
    /// <param name="value">The text.</param>
    public static implicit operator DateInput(string value) => new(value ?? throw new ArgumentNullException(nameof(value)), null, null, false);

    /// <summary>An instant, sent as UTC with milliseconds.</summary>
    /// <param name="value">The instant.</param>
    public static implicit operator DateInput(DateTimeOffset value) => new(null, value, null, false);

    /// <summary>A date and time; <see cref="DateTimeKind.Unspecified"/> is rejected when the call is made.</summary>
    /// <param name="value">The date and time.</param>
    public static implicit operator DateInput(DateTime value)
        => value.Kind == DateTimeKind.Unspecified
            ? new(null, null, null, true)
            : new(null, new DateTimeOffset(value.ToUniversalTime(), TimeSpan.Zero), null, false);

    /// <summary>A date, sent as <c>YYYY-MM-DD</c>.</summary>
    /// <param name="value">The date.</param>
    public static implicit operator DateInput(DateOnly value) => new(null, null, value, false);

    internal string Format(string param)
    {
        if (_unspecified)
        {
            throw new FinlightValidationException(
                $"{param}: a DateTime without a time zone (DateTimeKind.Unspecified) is ambiguous; use DateTimeOffset, a UTC DateTime or DateOnly",
                param);
        }

        if (_instant is { } instant)
        {
            return instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        if (_date is { } date)
        {
            return date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }

        return _text ?? throw new FinlightValidationException($"{param}: no value", param);
    }

    /// <inheritdoc />
    public bool Equals(DateInput other)
        => _text == other._text && _instant == other._instant && _date == other._date && _unspecified == other._unspecified;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is DateInput other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_text, _instant, _date, _unspecified);

    /// <summary>Compares two bounds.</summary>
    /// <param name="left">The first bound.</param>
    /// <param name="right">The second bound.</param>
    /// <returns>True when equal.</returns>
    public static bool operator ==(DateInput left, DateInput right) => left.Equals(right);

    /// <summary>Compares two bounds.</summary>
    /// <param name="left">The first bound.</param>
    /// <param name="right">The second bound.</param>
    /// <returns>True when different.</returns>
    public static bool operator !=(DateInput left, DateInput right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => _unspecified ? "(unspecified DateTime)" : Format("date");
}

/// <summary>
/// Body of <c>POST /v3/articles/search</c>. Every key is optional; only keys you set are sent (no
/// defaults are added). Vocabulary values accept any string; the server checks them.
/// </summary>
public sealed record SearchParams
{
    /// <summary><c>query</c>: query language, e.g. <c>ticker:NVDA AND eventType:earnings</c>.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    /// <summary><c>sources</c>: only these domains.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary><c>optInSources</c>: non-default sources added to the default pool.</summary>
    [JsonPropertyName("optInSources")]
    public IReadOnlyList<string>? OptInSources { get; init; }

    /// <summary><c>excludeSources</c>.</summary>
    [JsonPropertyName("excludeSources")]
    public IReadOnlyList<string>? ExcludeSources { get; init; }

    /// <summary>
    /// <c>sourceTypes</c>: only articles from sources of these types. Articles of unclassified sources
    /// (<see cref="ArticleSource.Type"/> null) never match. Filters inside the source pool and never adds a source to it.
    /// </summary>
    [JsonPropertyName("sourceTypes")]
    public IReadOnlyList<SourceType>? SourceTypes { get; init; }

    /// <summary>
    /// <c>excludeSourceTypes</c>: drop articles from sources of these types. Articles of unclassified sources are kept.
    /// </summary>
    [JsonPropertyName("excludeSourceTypes")]
    public IReadOnlyList<SourceType>? ExcludeSourceTypes { get; init; }

    /// <summary><c>from</c>: lower bound on the <see cref="OrderBy"/> timestamp.</summary>
    [JsonPropertyName("from")]
    public DateInput? From { get; init; }

    /// <summary><c>to</c>: upper bound on the <see cref="OrderBy"/> timestamp.</summary>
    [JsonPropertyName("to")]
    public DateInput? To { get; init; }

    /// <summary><c>languages</c>: ISO 639-1; must not be empty. The server default is <c>["en"]</c>.</summary>
    [JsonPropertyName("languages")]
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary><c>tickers</c>: <c>SYMBOL</c>, <c>SYMBOL@MIC</c> or vendor notations.</summary>
    [JsonPropertyName("tickers")]
    public IReadOnlyList<string>? Tickers { get; init; }

    /// <summary><c>countries</c>: the story's focus, ISO alpha-2.</summary>
    [JsonPropertyName("countries")]
    public IReadOnlyList<string>? Countries { get; init; }

    /// <summary><c>categories</c>.</summary>
    [JsonPropertyName("categories")]
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary><c>hasContent</c>: true keeps only articles with content.</summary>
    [JsonPropertyName("hasContent")]
    public bool? HasContent { get; init; }

    /// <summary><c>roles</c>, correlated with <see cref="Tickers"/> on the same company.</summary>
    [JsonPropertyName("roles")]
    public IReadOnlyList<CompanyRole>? Roles { get; init; }

    /// <summary><c>minRelevance</c>, 0 to 1.</summary>
    [JsonPropertyName("minRelevance")]
    public double? MinRelevance { get; init; }

    /// <summary><c>eventTypes</c>.</summary>
    [JsonPropertyName("eventTypes")]
    public IReadOnlyList<EventType>? EventTypes { get; init; }

    /// <summary><c>eventDirections</c>.</summary>
    [JsonPropertyName("eventDirections")]
    public IReadOnlyList<EventDirection>? EventDirections { get; init; }

    /// <summary><c>minImportance</c>.</summary>
    [JsonPropertyName("minImportance")]
    public Importance? MinImportance { get; init; }

    /// <summary><c>sectors</c> (GICS sector names).</summary>
    [JsonPropertyName("sectors")]
    public IReadOnlyList<string>? Sectors { get; init; }

    /// <summary><c>companyCountries</c>: where companies are domiciled.</summary>
    [JsonPropertyName("companyCountries")]
    public IReadOnlyList<string>? CompanyCountries { get; init; }

    /// <summary><c>exchanges</c>: primary-listing MICs.</summary>
    [JsonPropertyName("exchanges")]
    public IReadOnlyList<string>? Exchanges { get; init; }

    /// <summary><c>orderBy</c>; the server default is <c>publishedAt</c>.</summary>
    [JsonPropertyName("orderBy")]
    public OrderBy? OrderBy { get; init; }

    /// <summary><c>order</c>; the server default is <c>desc</c>.</summary>
    [JsonPropertyName("order")]
    public SortOrder? Order { get; init; }

    /// <summary><c>limit</c>: page size 1 to 100; the server default is 20.</summary>
    [JsonPropertyName("limit")]
    public int? Limit { get; init; }

    /// <summary><c>cursor</c>: <see cref="Pagination.NextCursor"/> of the previous page.</summary>
    [JsonPropertyName("cursor")]
    public string? Cursor { get; init; }

    /// <summary><c>include</c>: <c>content</c>, <c>companies</c>, <c>events</c> (at most 3).</summary>
    [JsonPropertyName("include")]
    public IReadOnlyList<IncludeBlock>? Include { get; init; }
}

/// <summary>Subscribe parameters of the enriched stream (<c>StreamAsync</c>).</summary>
public sealed record StreamParams
{
    /// <summary><c>query</c> (WebSocket uses the lenient v2 query grammar).</summary>
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    /// <summary><c>sources</c>.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary><c>excludeSources</c>.</summary>
    [JsonPropertyName("excludeSources")]
    public IReadOnlyList<string>? ExcludeSources { get; init; }

    /// <summary><c>optInSources</c>.</summary>
    [JsonPropertyName("optInSources")]
    public IReadOnlyList<string>? OptInSources { get; init; }

    /// <summary>
    /// <c>sourceTypes</c>: only articles from sources of these types; unclassified sources never match.
    /// Same vocabulary and meaning as <see cref="SearchParams.SourceTypes"/>.
    /// </summary>
    [JsonPropertyName("sourceTypes")]
    public IReadOnlyList<SourceType>? SourceTypes { get; init; }

    /// <summary>
    /// <c>excludeSourceTypes</c>: drop articles from sources of these types; unclassified sources are kept.
    /// </summary>
    [JsonPropertyName("excludeSourceTypes")]
    public IReadOnlyList<SourceType>? ExcludeSourceTypes { get; init; }

    /// <summary><c>language</c>: one language; the server default is <c>en</c>.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary><c>tickers</c>.</summary>
    [JsonPropertyName("tickers")]
    public IReadOnlyList<string>? Tickers { get; init; }

    /// <summary><c>countries</c>.</summary>
    [JsonPropertyName("countries")]
    public IReadOnlyList<string>? Countries { get; init; }

    /// <summary><c>categories</c>.</summary>
    [JsonPropertyName("categories")]
    public IReadOnlyList<string>? Categories { get; init; }

    /// <summary><c>excludeEmptyContent</c>.</summary>
    [JsonPropertyName("excludeEmptyContent")]
    public bool? ExcludeEmptyContent { get; init; }

    /// <summary><c>includeUpdates</c>: without it the server never sends <c>article.updated</c>.</summary>
    [JsonPropertyName("includeUpdates")]
    public bool? IncludeUpdates { get; init; }

    /// <summary><c>include</c>: <c>content</c>, <c>companies</c>, <c>events</c>.</summary>
    [JsonPropertyName("include")]
    public IReadOnlyList<IncludeBlock>? Include { get; init; }
}

/// <summary>Subscribe parameters of the raw stream (<c>RawStreamAsync</c>). Raw has no <c>include</c>.</summary>
public sealed record RawStreamParams
{
    /// <summary><c>query</c>.</summary>
    [JsonPropertyName("query")]
    public string? Query { get; init; }

    /// <summary><c>sources</c>.</summary>
    [JsonPropertyName("sources")]
    public IReadOnlyList<string>? Sources { get; init; }

    /// <summary><c>excludeSources</c>.</summary>
    [JsonPropertyName("excludeSources")]
    public IReadOnlyList<string>? ExcludeSources { get; init; }

    /// <summary>
    /// <c>sourceTypes</c>: only articles from sources of these types; unclassified sources never match.
    /// Same vocabulary and meaning as <see cref="SearchParams.SourceTypes"/>.
    /// </summary>
    [JsonPropertyName("sourceTypes")]
    public IReadOnlyList<SourceType>? SourceTypes { get; init; }

    /// <summary>
    /// <c>excludeSourceTypes</c>: drop articles from sources of these types; unclassified sources are kept.
    /// </summary>
    [JsonPropertyName("excludeSourceTypes")]
    public IReadOnlyList<SourceType>? ExcludeSourceTypes { get; init; }

    /// <summary><c>language</c>.</summary>
    [JsonPropertyName("language")]
    public string? Language { get; init; }

    /// <summary><c>includeUpdates</c>.</summary>
    [JsonPropertyName("includeUpdates")]
    public bool? IncludeUpdates { get; init; }
}

/// <summary>Parameters of <c>companies.search</c> (<c>GET /v3/companies?q=…&amp;mic=…</c>).</summary>
public sealed record CompanySearchParams
{
    /// <summary><c>q</c>: ticker, name, alias, ISIN, <c>XNAS:AMD</c> or vendor notations; at most 200 characters.</summary>
    [JsonPropertyName("q")]
    public required string Q { get; init; }

    /// <summary><c>mic</c>: MIC or alias narrowing the hits; at most 32 characters.</summary>
    [JsonPropertyName("mic")]
    public string? Mic { get; init; }
}

/// <summary>Options of <c>articles.get</c> and <c>articles.getByLink</c>.</summary>
public sealed record LookupOptions
{
    /// <summary><c>include</c>: sent comma-joined; empty sends nothing.</summary>
    public IReadOnlyList<IncludeBlock>? Include { get; init; }
}

/// <summary>Options of <c>companies.get</c>.</summary>
public sealed record CompanyGetOptions
{
    /// <summary>Your own ETag. On 304 the call returns null ("not modified"), never an error.</summary>
    public string? IfNoneMatch { get; init; }
}

/// <summary>Options of <c>articles.searchAll</c>.</summary>
public sealed record SearchAllOptions
{
    /// <summary>Page limit for this call (default: <see cref="FinlightConfig.MaxPages"/>). Every page costs quota.</summary>
    public int? MaxPages { get; init; }
}
