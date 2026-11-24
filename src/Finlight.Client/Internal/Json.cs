using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Finlight.Internal;

/// <summary>Serializer settings of the SDK: exact key names, no coercion, no global enum converter.</summary>
internal static class FinlightJson
{
    public static JsonSerializerOptions Options { get; } = new()
    {
        PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>Reads a list that may be absent or null as <c>[]</c> (R-MODELS-1).</summary>
/// <typeparam name="T">The element type.</typeparam>
internal sealed class NullAsEmptyListConverter<T> : JsonConverter<IReadOnlyList<T>>
{
    public override bool HandleNull => true;

    public override IReadOnlyList<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return [];
        }

        return JsonSerializer.Deserialize<List<T>>(ref reader, options) ?? [];
    }

    public override void Write(Utf8JsonWriter writer, IReadOnlyList<T> value, JsonSerializerOptions options)
        => JsonSerializer.Serialize(writer, value, typeof(IReadOnlyList<T>), options);
}

/// <summary>Strict RFC 3339 timestamps (R-MODELS-2): date, time, optional fraction, then <c>Z</c> or an offset.</summary>
internal static partial class Rfc3339
{
    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}[Tt]\d{2}:\d{2}:\d{2}(\.\d{1,9})?([Zz]|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex Pattern();

    public static DateTimeOffset? Parse(string? value)
    {
        if (value is null || !Pattern().IsMatch(value))
        {
            return null;
        }

        // DateTimeOffset keeps 7 fraction digits; cut longer fractions instead of rounding.
        var text = value.ToUpperInvariant();
        var dot = text.IndexOf('.', StringComparison.Ordinal);
        if (dot >= 0)
        {
            var end = dot + 1;
            while (end < text.Length && char.IsAsciiDigit(text[end]))
            {
                end++;
            }

            if (end - dot - 1 > 7)
            {
                text = string.Concat(text.AsSpan(0, dot + 8), text.AsSpan(end));
            }
        }

        string[] formats = ["yyyy-MM-dd'T'HH:mm:ss.FFFFFFFK", "yyyy-MM-dd'T'HH:mm:ssK"];
        return DateTimeOffset.TryParseExact(
            text,
            formats,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out var parsed)
            ? parsed
            : null;
    }
}
