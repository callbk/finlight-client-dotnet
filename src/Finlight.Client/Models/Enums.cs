using System.Text.Json.Serialization;
using Finlight.Json;

namespace Finlight;

/// <summary>An article category assigned by finlight's classification.</summary>
public enum Category
{
    /// <summary>Financial markets.</summary>
    Markets,

    /// <summary>Macroeconomics.</summary>
    Economy,

    /// <summary>Company and business news.</summary>
    Business,

    /// <summary>Domestic politics.</summary>
    Politics,

    /// <summary>International relations and conflicts.</summary>
    Geopolitics,

    /// <summary>Regulation and legislation.</summary>
    Regulation,

    /// <summary>Technology.</summary>
    Technology,

    /// <summary>Energy.</summary>
    Energy,

    /// <summary>Commodities.</summary>
    Commodities,

    /// <summary>Cryptocurrencies.</summary>
    Crypto,

    /// <summary>Health and pharma.</summary>
    Health,

    /// <summary>Climate.</summary>
    Climate,

    /// <summary>Security and defense.</summary>
    Security,
}

/// <summary>The sort field for article queries.</summary>
public enum ArticleOrderBy
{
    /// <summary>Order by publish date.</summary>
    PublishDate,

    /// <summary>Order by creation date.</summary>
    CreatedAt,

    /// <summary>Order by revision date.</summary>
    RevisedDate,
}

/// <summary>The sort direction for article queries.</summary>
[JsonConverter(typeof(SortOrderConverter))]
public enum SortOrder
{
    /// <summary>Ascending.</summary>
    Asc,

    /// <summary>Descending.</summary>
    Desc,
}
