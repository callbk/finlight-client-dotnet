using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finlight.Json;

/// <summary>Central serializer configuration for all finlight wire traffic.</summary>
internal static class FinlightJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        };
        options.Converters.Add(new FlexibleDateTimeOffsetConverter());
        options.Converters.Add(new StringOrNumberDoubleConverter());
        // Converters registered here take precedence over [JsonConverter] type
        // attributes, so SortOrder ("ASC"/"DESC") must precede the camelCase
        // enum fallback used by Category and ArticleOrderBy.
        options.Converters.Add(new SortOrderConverter());
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}
