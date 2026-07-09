using System.Text.Json;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Serialization;

/// <summary>
/// Canonical System.Text.Json format for PositionActor sharded envelopes, commands, persisted events
/// and snapshots (US-F0-06-T05b). It reuses the protocol-wide ADR-007 converters and adds explicit
/// manifests for the PositionActor's closed command/event unions.
/// </summary>
internal static class PositionProtocolJsonFormat
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options);
    }

    public static object Deserialize(string manifest, ReadOnlySpan<byte> payload)
    {
        var type = PositionProtocolManifests.ForManifest(manifest);
        return JsonSerializer.Deserialize(payload, type, Options)
            ?? throw new JsonException($"Payload for manifest '{manifest}' deserialized to null.");
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = OrgMessageJsonFormat.CreateOptions();

        options.Converters.Add(new GuidIdJsonConverter<PositionTaskId>(PositionTaskId.From, id => id.Value));
        options.Converters.Add(new OccupantTypeJsonConverter());
        options.Converters.Add(new MessageProcessingCompletionStatusJsonConverter());
        options.Converters.Add(new PositionSnapshotJsonConverter());
        options.Converters.Add(new ManifestedPayloadJsonConverter<OrgMessage>(
            OrgMessageManifests.ForType,
            OrgMessageManifests.ForManifest));
        options.Converters.Add(new ManifestedPayloadJsonConverter<PositionCommand>(
            PositionProtocolManifests.ForType,
            PositionProtocolManifests.ForManifest));
        options.Converters.Add(new ManifestedPayloadJsonConverter<PositionEvent>(
            PositionProtocolManifests.ForType,
            PositionProtocolManifests.ForManifest));

        return options;
    }
}
