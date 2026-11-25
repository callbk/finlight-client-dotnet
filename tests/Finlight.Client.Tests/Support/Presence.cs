using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Finlight.Internal;

namespace Finlight.Tests.Support;

/// <summary>
/// Reads the presence state of a wire path (<c>companies[0].primaryListing.mic</c>) through the public
/// model: the property whose <see cref="JsonPropertyNameAttribute"/> is exactly the wire key, then
/// <see cref="Optional{T}.IsPresent"/> for gated keys. States: absent, null, empty, value.
/// </summary>
internal static partial class Presence
{
    public static string Of(object model, string path)
    {
        object? current = model;
        var segments = path.Split('.');
        for (var i = 0; i < segments.Length; i++)
        {
            var match = Segment().Match(segments[i]);
            var key = match.Groups[1].Value;
            if (current is null)
            {
                throw new InvalidOperationException($"{path}: parent of {key} is null");
            }

            var property = WireProperty(current.GetType(), key);
            var value = property.GetValue(current);
            if (IsOptional(property.PropertyType))
            {
                var present = (bool)property.PropertyType.GetProperty("IsPresent")!.GetValue(value)!;
                if (!present)
                {
                    if (i < segments.Length - 1 || match.Groups[2].Success)
                    {
                        throw new InvalidOperationException($"{path}: {key} is absent");
                    }

                    return "absent";
                }

                value = property.PropertyType.GetProperty("Value")!.GetValue(value);
            }

            if (match.Groups[2].Success)
            {
                var index = int.Parse(match.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
                value = ((IList)value!)[index];
            }

            current = value;
        }

        return current switch
        {
            null => "null",
            string => "value",
            ICollection { Count: 0 } => "empty",
            _ => "value",
        };
    }

    public static PropertyInfo WireProperty(Type type, string key)
        => type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .SingleOrDefault(p => p.GetCustomAttribute<JsonPropertyNameAttribute>()?.Name == key)
            ?? throw new InvalidOperationException($"{type.Name} has no property for the wire key '{key}'");

    public static bool IsOptional(Type type) => type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Optional<>);

    /// <summary>Decodes an article the way the SDK does for a page entry.</summary>
    public static Article Article(JsonNode wire)
    {
        using var doc = JsonDocument.Parse(wire.ToJsonString());
        return Decode.Article(doc.RootElement, "$");
    }

    /// <summary>The model written back as JSON with plain <see cref="JsonSerializer"/>.</summary>
    public static string Roundtrip(object model) => JsonSerializer.Serialize(model, model.GetType());

    [GeneratedRegex(@"^([A-Za-z]+)(?:\[(\d+)\])?$")]
    private static partial Regex Segment();
}
