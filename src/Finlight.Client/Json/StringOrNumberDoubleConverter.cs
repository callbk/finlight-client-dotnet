using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finlight.Json;

/// <summary>
/// Reads doubles from JSON numbers or strings. The API and webhooks deliver
/// confidence values in both representations ("0.95" and 0.95). Always writes
/// numbers.
/// </summary>
internal sealed class StringOrNumberDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
        {
            var value = reader.GetString();
            if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            {
                throw new JsonException($"finlight: cannot parse '{value}' as a number.");
            }

            return parsed;
        }

        return reader.GetDouble();
    }

    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
        => writer.WriteNumberValue(value);
}
