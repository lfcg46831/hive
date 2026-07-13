using System.Text.Json;
using System.Text.Json.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Serialization;

/// <summary>
/// Explicit converter for <see cref="PositionSnapshot"/>. Its public shape uses immutable
/// collections, while the constructor accepts general collection interfaces so it can validate and
/// normalize null-as-empty. System.Text.Json cannot bind that constructor directly.
/// </summary>
internal sealed class PositionSnapshotJsonConverter : JsonConverter<PositionSnapshot>
{
    public override PositionSnapshot Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        var dto = JsonSerializer.Deserialize<PositionSnapshotData>(ref reader, options)
            ?? throw new JsonException("PositionSnapshot payload deserialized to null.");

        return new PositionSnapshot(
            dto.TakenAt,
            dto.Occupant,
            dto.OccupantType,
            dto.Inbox,
            dto.OpenTasks,
            dto.ShortMemory,
            dto.RecentHistory,
            dto.ProcessedMessages,
            dto.LastConfigurationStamp,
            dto.RetainedActions,
            dto.ShortMemoryContextScopes);
    }

    public override void Write(
        Utf8JsonWriter writer,
        PositionSnapshot value,
        JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(value);

        var dto = new PositionSnapshotData
        {
            TakenAt = value.TakenAt,
            Occupant = value.Occupant,
            OccupantType = value.OccupantType,
            Inbox = value.Inbox.ToList(),
            OpenTasks = value.OpenTasks.ToList(),
            ShortMemory = value.ShortMemory.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal),
            RecentHistory = value.RecentHistory.ToList(),
            ProcessedMessages = value.ProcessedMessages.ToList(),
            LastConfigurationStamp = value.LastConfigurationStamp,
            RetainedActions = value.RetainedActions.IsEmpty ? null : value.RetainedActions.ToList(),
            ShortMemoryContextScopes = value.ShortMemoryContextScopes.IsEmpty
                ? null
                : value.ShortMemoryContextScopes.ToDictionary(
                    pair => pair.Key,
                    pair => pair.Value,
                    StringComparer.Ordinal),
        };

        JsonSerializer.Serialize(writer, dto, options);
    }

    private sealed class PositionSnapshotData
    {
        public DateTimeOffset TakenAt { get; set; }

        public OccupantId? Occupant { get; set; }

        public OccupantType? OccupantType { get; set; }

        public List<OrgMessage>? Inbox { get; set; }

        public List<PersistedTask>? OpenTasks { get; set; }

        public Dictionary<string, string>? ShortMemory { get; set; }

        public List<MessageId>? RecentHistory { get; set; }

        public List<MessageId>? ProcessedMessages { get; set; }

        public PositionConfigurationStamp? LastConfigurationStamp { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public List<PersistedRetainedAction>? RetainedActions { get; set; }

        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public Dictionary<string, ShortMemoryContextScope>? ShortMemoryContextScopes { get; set; }
    }
}
