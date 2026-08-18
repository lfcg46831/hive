using Akka.Actor;
using Akka.Serialization;

namespace Hive.Actors.Serialization;

/// <summary>
/// Akka serializer for the sharded AI gateway protocol (US-F1-05-T07): envelopes, commands and
/// replies use the versionable ADR-007 JSON format with stable manifests, never Akka's default
/// .NET serialization.
/// </summary>
public sealed class AiGatewayProtocolJsonSerializer : SerializerWithStringManifest
{
    /// <summary>
    /// Stable serializer identifier. The value spells "HIAG" in ASCII (0x48 0x49 0x41 0x47) and
    /// must not change once nodes of different versions exchange gateway messages.
    /// </summary>
    public const int SerializerId = 0x48494147;

    public AiGatewayProtocolJsonSerializer(ExtendedActorSystem system)
        : base(system)
    {
    }

    public override int Identifier => SerializerId;

    public override string Manifest(object o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return AiGatewayProtocolManifests.ForType(o.GetType());
    }

    public override byte[] ToBinary(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return AiGatewayProtocolJsonFormat.Serialize(obj);
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(manifest);
        return AiGatewayProtocolJsonFormat.Deserialize(manifest, bytes);
    }
}
