using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hive.Actors.Serialization;

/// <summary>
/// Serializes a structural (string-based) identity value object as its raw textual value,
/// reusing the value object's own factory so all domain invariants are enforced on read.
/// Invalid payloads surface as <see cref="JsonException"/> rather than silent defaults,
/// per ADR-007.
/// </summary>
internal sealed class StructuralIdJsonConverter<T> : JsonConverter<T>
    where T : class
{
    private readonly Func<string, T> _fromValue;
    private readonly Func<T, string> _toValue;

    public StructuralIdJsonConverter(Func<string, T> fromValue, Func<T, string> toValue)
    {
        _fromValue = fromValue;
        _toValue = toValue;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a string value for {typeof(T).Name}.");
        }

        var raw = reader.GetString();

        try
        {
            return _fromValue(raw!);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                $"'{raw}' is not a valid {typeof(T).Name}.",
                exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(_toValue(value));
    }
}

/// <summary>
/// Serializes a <see cref="Guid"/>-based identity value object as a canonical Guid string,
/// reusing the value object's factory so the empty-Guid invariant is enforced on read.
/// </summary>
internal sealed class GuidIdJsonConverter<T> : JsonConverter<T>
    where T : class
{
    private readonly Func<Guid, T> _fromValue;
    private readonly Func<T, Guid> _toValue;

    public GuidIdJsonConverter(Func<Guid, T> fromValue, Func<T, Guid> toValue)
    {
        _fromValue = fromValue;
        _toValue = toValue;
    }

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException($"Expected a Guid string for {typeof(T).Name}.");
        }

        if (!reader.TryGetGuid(out var value))
        {
            throw new JsonException($"'{reader.GetString()}' is not a valid Guid for {typeof(T).Name}.");
        }

        try
        {
            return _fromValue(value);
        }
        catch (ArgumentException exception)
        {
            throw new JsonException(
                $"'{value}' is not a valid {typeof(T).Name}.",
                exception);
        }
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStringValue(_toValue(value));
    }
}
