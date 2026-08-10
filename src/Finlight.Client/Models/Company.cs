namespace Finlight;

/// <summary>A company recognized in an article.</summary>
public sealed class Company
{
    /// <summary>finlight's stable company identifier.</summary>
    public required int CompanyId { get; init; }

    /// <summary>Company name.</summary>
    public required string Name { get; init; }

    /// <summary>Primary ticker symbol.</summary>
    public required string Ticker { get; init; }

    /// <summary>Confidence of the entity match (0–1).</summary>
    public double? Confidence { get; init; }

    /// <summary>Country of the company.</summary>
    public string? Country { get; init; }

    /// <summary>Primary exchange.</summary>
    public string? Exchange { get; init; }

    /// <summary>Industry classification.</summary>
    public string? Industry { get; init; }

    /// <summary>Sector classification.</summary>
    public string? Sector { get; init; }

    /// <summary>Primary ISIN.</summary>
    public string? Isin { get; init; }

    /// <summary>OpenFIGI identifier.</summary>
    public string? Openfigi { get; init; }

    /// <summary>The company's primary exchange listing.</summary>
    public Listing? PrimaryListing { get; init; }

    /// <summary>All known ISINs.</summary>
    public IReadOnlyList<string>? Isins { get; init; }

    /// <summary>Further exchange listings.</summary>
    public IReadOnlyList<Listing>? OtherListings { get; init; }
}
