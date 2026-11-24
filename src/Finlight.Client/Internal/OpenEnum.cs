using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finlight.Internal;

/// <summary>An open vocabulary: a string value plus known constants (R-ENUMS-1).</summary>
/// <typeparam name="TSelf">The vocabulary type.</typeparam>
internal interface IOpenEnum<TSelf>
    where TSelf : struct, IOpenEnum<TSelf>
{
    public string Value { get; }

    public static abstract TSelf Create(string value);
}

/// <summary>Reads and writes an open vocabulary as a JSON string; any string is kept as sent.</summary>
/// <typeparam name="T">The vocabulary type.</typeparam>
internal sealed class OpenEnumConverter<T> : JsonConverter<T>
    where T : struct, IOpenEnum<T>
{
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"expected a string, got {reader.TokenType}");
        }

        return T.Create(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.Value);
}
