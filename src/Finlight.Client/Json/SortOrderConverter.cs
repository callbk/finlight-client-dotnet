using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finlight.Json;

/// <summary>
/// Serializes <see cref="SortOrder"/> as the uppercase "ASC"/"DESC" the API
/// expects (the camelCase enum policy would emit "asc").
/// </summary>
internal sealed class SortOrderConverter : JsonConverter<SortOrder>
{
    public override SortOrder Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()?.ToUpperInvariant() switch
        {
            "ASC" => SortOrder.Asc,
            "DESC" => SortOrder.Desc,
            var value => throw new JsonException($"finlight: unknown sort order '{value}'."),
        };

    public override void Write(Utf8JsonWriter writer, SortOrder value, JsonSerializerOptions options)
        => writer.WriteStringValue(value == SortOrder.Asc ? "ASC" : "DESC");
}
