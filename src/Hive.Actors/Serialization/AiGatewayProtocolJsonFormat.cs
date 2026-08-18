using System.Text.Json;
using Hive.Actors.Gateway;

namespace Hive.Actors.Serialization;

/// <summary>
/// Canonical System.Text.Json format for the sharded AI gateway protocol (US-F1-05-T07). It reuses
/// the protocol-wide ADR-007 converters — identities, tolerant reads, no computed properties — and
/// adds the AI contracts' own converters, so <c>AiGatewayRequest</c>/<c>AiGatewayResponse</c> cross
/// the wire without any CLR type name and without a second, drifting shape.
/// </summary>
internal static class AiGatewayProtocolJsonFormat
{
    public static JsonSerializerOptions Options { get; } = CreateOptions();

    public static byte[] Serialize(object value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return JsonSerializer.SerializeToUtf8Bytes(value, value.GetType(), Options);
    }

    public static object Deserialize(string manifest, ReadOnlySpan<byte> payload)
    {
        var type = AiGatewayProtocolManifests.ForManifest(manifest);
        return JsonSerializer.Deserialize(payload, type, Options)
            ?? throw new JsonException($"Payload for manifest '{manifest}' deserialized to null.");
    }

    internal static JsonSerializerOptions CreateOptions()
    {
        var options = OrgMessageJsonFormat.CreateOptions();

        // Protocol enums of the AI contracts as canonical wire values (§9.5).
        options.Converters.Add(new AiProcessingModeJsonConverter());
        options.Converters.Add(new AiFinishReasonJsonConverter());
        options.Converters.Add(new AiOutputConstraintModeJsonConverter());
        options.Converters.Add(new AiGatewayErrorCodeJsonConverter());
        options.Converters.Add(new AiGatewayErrorReasonJsonConverter());
        options.Converters.Add(new AiGatewayMessageRoleJsonConverter());

        // Records System.Text.Json cannot bind through a constructor.
        options.Converters.Add(new AiGatewayPolicyJsonConverter());
        options.Converters.Add(new AiGatewayErrorJsonConverter());
        options.Converters.Add(new AiGatewayResponseJsonConverter());

        // The closed command union nested inside the sharded envelope.
        options.Converters.Add(new ManifestedPayloadJsonConverter<AiGatewayProviderCommand>(
            AiGatewayProtocolManifests.ForType,
            AiGatewayProtocolManifests.ForManifest));

        return options;
    }
}
