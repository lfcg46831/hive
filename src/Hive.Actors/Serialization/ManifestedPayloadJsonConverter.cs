using System.Text.Json;
using System.Text.Json.Serialization;
using Akka.Serialization;

namespace Hive.Actors.Serialization;

/// <summary>
/// Serializes an abstract protocol base type as an explicit stable manifest plus its concrete
/// payload. This is used only for polymorphic values nested inside another persisted contract.
/// Top-level Akka serialization gets the manifest from <see cref="SerializerWithStringManifest"/>.
/// </summary>
internal sealed class ManifestedPayloadJsonConverter<TBase> : JsonConverter<TBase>
    where TBase : class
{
    private const string ManifestProperty = "manifest";
    private const string PayloadProperty = "payload";

    private readonly Func<Type, string> _manifestForType;
    private readonly Func<string, Type> _typeForManifest;

    public ManifestedPayloadJsonConverter(
        Func<Type, string> manifestForType,
        Func<string, Type> typeForManifest)
    {
        _manifestForType = manifestForType;
        _typeForManifest = typeForManifest;
    }

    public override TBase Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException($"Expected an object for manifested {typeof(TBase).Name}.");
        }

        using var document = JsonDocument.ParseValue(ref reader);
        var root = document.RootElement;
        var manifest = ReadManifest(root);
        var payload = ReadPayload(root);
        var concreteType = _typeForManifest(manifest);

        return (TBase)(payload.Deserialize(concreteType, options)
            ?? throw new JsonException($"Payload for manifest '{manifest}' deserialized to null."));
    }

    public override void Write(Utf8JsonWriter writer, TBase value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        writer.WriteStartObject();
        writer.WriteString(ManifestProperty, _manifestForType(value.GetType()));
        writer.WritePropertyName(PayloadProperty);
        JsonSerializer.Serialize(writer, value, value.GetType(), options);
        writer.WriteEndObject();
    }

    private static string ReadManifest(JsonElement root)
    {
        if (!TryGetProperty(root, ManifestProperty, out var element) ||
            element.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new JsonException($"Manifested {typeof(TBase).Name} is missing a string manifest.");
        }

        return element.GetString()!;
    }

    private static JsonElement ReadPayload(JsonElement root)
    {
        if (!TryGetProperty(root, PayloadProperty, out var element))
        {
            throw new JsonException($"Manifested {typeof(TBase).Name} is missing a payload.");
        }

        return element;
    }

    private static bool TryGetProperty(JsonElement root, string name, out JsonElement value)
    {
        if (root.TryGetProperty(name, out value))
        {
            return true;
        }

        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }
}
