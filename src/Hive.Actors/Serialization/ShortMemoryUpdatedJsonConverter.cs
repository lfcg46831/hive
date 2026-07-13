using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Positions;

namespace Hive.Actors.Serialization;

/// <summary>
/// Keeps the v1 short-memory event payload stable when scope is absent while allowing newer events
/// to persist the additive AI-context scope.
/// </summary>
internal sealed class ShortMemoryUpdatedJsonConverter : JsonConverter<ShortMemoryUpdated>
{
    public override ShortMemoryUpdated Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var data = JsonSerializer.Deserialize<ShortMemoryUpdatedData>(ref reader, options)
            ?? throw new JsonException("ShortMemoryUpdated payload deserialized to null.");

        return new ShortMemoryUpdated(
            data.Key ?? throw new JsonException("ShortMemoryUpdated requires Key."),
            data.Value ?? throw new JsonException("ShortMemoryUpdated requires Value."),
            data.OccurredAt,
            data.ContextScope);
    }

    public override void Write(
        Utf8JsonWriter writer,
        ShortMemoryUpdated value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        JsonSerializer.Serialize(
            writer,
            new ShortMemoryUpdatedData
            {
                Key = value.Key,
                Value = value.Value,
                OccurredAt = value.OccurredAt,
                ContextScope = value.ContextScope,
            },
            options);
    }

    private sealed class ShortMemoryUpdatedData
    {
        public string? Key { get; set; }

        public string? Value { get; set; }

        public DateTimeOffset OccurredAt { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ShortMemoryContextScope? ContextScope { get; set; }
    }
}
