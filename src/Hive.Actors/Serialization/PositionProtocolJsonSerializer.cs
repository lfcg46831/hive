using Akka.Actor;
using Akka.Serialization;

namespace Hive.Actors.Serialization;

/// <summary>
/// Akka serializer for the PositionActor protocol (US-F0-06-T05b): sharded envelopes, internal
/// commands, persisted events and snapshots use the versionable ADR-007 JSON format with stable
/// manifests, never Akka's default .NET serialization.
/// </summary>
public sealed class PositionProtocolJsonSerializer : SerializerWithStringManifest
{
    /// <summary>
    /// Stable serializer identifier. The value spells "HIVP" in ASCII (0x48 0x49 0x56 0x50) and
    /// must not change once PositionActor journals exist.
    /// </summary>
    public const int SerializerId = 0x48495650;

    public PositionProtocolJsonSerializer(ExtendedActorSystem system)
        : base(system)
    {
    }

    public override int Identifier => SerializerId;

    public override string Manifest(object o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return PositionProtocolManifests.ForType(o.GetType());
    }

    public override byte[] ToBinary(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return PositionProtocolJsonFormat.Serialize(obj);
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(manifest);
        return PositionProtocolJsonFormat.Deserialize(manifest, bytes);
    }
}
