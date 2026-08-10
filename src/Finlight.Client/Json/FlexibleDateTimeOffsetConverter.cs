using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finlight.Json;

/// <summary>
/// Parses the timestamp formats used by the finlight API: RFC 3339 with or
/// without zone, space-separated datetimes, and date-only strings. The wire
/// can carry up to 9 fractional-second digits (nanoseconds); .NET parses at
/// most 7, so longer fractions are truncated first. Zone-less timestamps are
/// interpreted as UTC.
/// </summary>
internal sealed class FlexibleDateTimeOffsetConverter : JsonConverter<DateTimeOffset>
{
    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var value = reader.GetString();
        if (value is null || !TryParse(value, out var parsed))
        {
            throw new JsonException($"finlight: cannot parse '{value}' as a timestamp.");
        }

        return parsed;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString("O", CultureInfo.InvariantCulture));

    internal static bool TryParse(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            TruncateFraction(value.Trim()),
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out parsed);

    /// <summary>Truncates fractional seconds to the 7 digits .NET can parse.</summary>
    private static string TruncateFraction(string value)
    {
        var dot = value.IndexOf('.');
        if (dot < 0)
        {
            return value;
        }

        var digitsEnd = dot + 1;
        while (digitsEnd < value.Length && char.IsAsciiDigit(value[digitsEnd]))
        {
            digitsEnd++;
        }

        const int maxFractionDigits = 7;
        return digitsEnd - dot - 1 <= maxFractionDigits
            ? value
            : value[..(dot + 1 + maxFractionDigits)] + value[digitsEnd..];
    }
}
