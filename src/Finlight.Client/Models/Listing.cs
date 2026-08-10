namespace Finlight;

/// <summary>One exchange listing of a company.</summary>
public sealed class Listing
{
    /// <summary>Ticker symbol on this exchange.</summary>
    public required string Ticker { get; init; }

    /// <summary>Exchange code, e.g. "XNAS".</summary>
    public required string ExchangeCode { get; init; }

    /// <summary>Country of the exchange.</summary>
    public required string ExchangeCountry { get; init; }
}
