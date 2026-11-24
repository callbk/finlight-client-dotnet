using System.Text.Json;
using System.Text.Json.Serialization;

namespace Finlight;

/// <summary>
/// A wire key that depends on the plan or on <c>include</c> (R-NULL-1). It has three states:
/// absent (<see cref="IsPresent"/> is false: not entitled, not requested, or raw), present with
/// <c>null</c> (entitled and requested, but no value exists), and present with a value.
/// </summary>
/// <remarks>
/// <c>default(Optional&lt;T&gt;)</c> is absent. When a model is written back as JSON with
/// <see cref="JsonSerializer"/>, absent keys stay absent and <c>null</c> stays <c>null</c>.
/// The converter is attached to the type, so it works with any <see cref="JsonSerializerOptions"/>.
/// </remarks>
/// <typeparam name="T">The value type.</typeparam>
[JsonConverter(typeof(OptionalConverterFactory))]
public readonly struct Optional<T> : IEquatable<Optional<T>>
{
    private readonly T? _value;

    /// <summary>Creates a present key holding <paramref name="value"/> (which may be <c>null</c>).</summary>
    /// <param name="value">The value, or <c>null</c> for a key that is present with <c>null</c>.</param>
    public Optional(T? value)
    {
        _value = value;
        IsPresent = true;
    }

    /// <summary>The absent state (the key was not in the JSON).</summary>
    public static Optional<T> Absent => default;

    /// <summary>True when the key was in the JSON, also when its value is <c>null</c>.</summary>
    public bool IsPresent { get; }

    /// <summary>True when the key was in the JSON with a non-null value.</summary>
    public bool HasValue => IsPresent && _value is not null;

    /// <summary>The value; <c>null</c> when absent or when present with <c>null</c>.</summary>
    public T? Value => _value;

    /// <summary>Returns the value, or <paramref name="fallback"/> when absent or <c>null</c>.</summary>
    /// <param name="fallback">The value to use instead.</param>
    /// <returns>The value or the fallback.</returns>
    public T? GetValueOrDefault(T? fallback = default) => HasValue ? _value : fallback;

    /// <summary>Wraps a value as a present key.</summary>
    /// <param name="value">The value.</param>
    public static implicit operator Optional<T>(T? value) => new(value);

    /// <inheritdoc />
    public bool Equals(Optional<T> other)
        => IsPresent == other.IsPresent && EqualityComparer<T?>.Default.Equals(_value, other._value);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is Optional<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(IsPresent, _value);

    /// <summary>Compares two keys by state and value.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns>True when both have the same state and value.</returns>
    public static bool operator ==(Optional<T> left, Optional<T> right) => left.Equals(right);

    /// <summary>Compares two keys by state and value.</summary>
    /// <param name="left">The first key.</param>
    /// <param name="right">The second key.</param>
    /// <returns>True when state or value differ.</returns>
    public static bool operator !=(Optional<T> left, Optional<T> right) => !left.Equals(right);

    /// <inheritdoc />
    public override string ToString() => !IsPresent ? "(absent)" : _value?.ToString() ?? "null";
}

/// <summary>System.Text.Json support for <see cref="Optional{T}"/>.</summary>
internal sealed class OptionalConverterFactory : JsonConverterFactory
{
    /// <inheritdoc />
    public override bool CanConvert(Type typeToConvert)
        => typeToConvert.IsGenericType && typeToConvert.GetGenericTypeDefinition() == typeof(Optional<>);

    /// <inheritdoc />
    public override JsonConverter CreateConverter(Type typeToConvert, JsonSerializerOptions options)
    {
        var inner = typeToConvert.GetGenericArguments()[0];
        return (JsonConverter)Activator.CreateInstance(typeof(OptionalConverter<>).MakeGenericType(inner))!;
    }

    private sealed class OptionalConverter<T> : JsonConverter<Optional<T>>
    {
        // A JSON null must reach Read: it is the "present with null" state.
        public override bool HandleNull => true;

        public override Optional<T> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            if (reader.TokenType == JsonTokenType.Null)
            {
                return new Optional<T>(default);
            }

            return new Optional<T>(JsonSerializer.Deserialize<T>(ref reader, options));
        }

        public override void Write(Utf8JsonWriter writer, Optional<T> value, JsonSerializerOptions options)
        {
            // Only reached for present keys: the properties carry WhenWritingDefault, so absent is skipped.
            if (value.Value is null)
            {
                writer.WriteNullValue();
                return;
            }

            JsonSerializer.Serialize(writer, value.Value, options);
        }
    }
}
