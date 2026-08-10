namespace Finlight;

/// <summary>A news source available through the API.</summary>
public sealed class Source
{
    /// <summary>Source domain, e.g. "www.reuters.com".</summary>
    public required string Domain { get; init; }

    /// <summary>Whether full article content is available for this source.</summary>
    public bool IsContentAvailable { get; init; }

    /// <summary>Whether the source is part of the default source set.</summary>
    public bool IsDefaultSource { get; init; }

    /// <summary>ISO 3166-1 alpha-2 country the source originates from.</summary>
    public string? OriginCountry { get; init; }

    /// <summary>Languages published by this source.</summary>
    public IReadOnlyList<string>? Languages { get; init; }

    /// <summary>Whether this is a custom source set up for your account.</summary>
    public bool? IsCustomSource { get; init; }
}
