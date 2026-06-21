using Akka.Actor;
using Akka.Serialization;

namespace Hive.Actors.Serialization;

/// <summary>
/// Akka serializer (US-F0-03-T08) that binds the organizational message protocol to the
/// versionable System.Text.Json format chosen in ADR-007, replacing Akka's default .NET/Newtonsoft
/// serialization for these types. It uses a string manifest — the canonical message-type token from
/// <see cref="OrgMessageManifests"/> — so the same format serves both remote/cluster messages and
/// persisted events/snapshots without leaking CLR type names onto the wire.
/// </summary>
public sealed class OrgMessageJsonSerializer : SerializerWithStringManifest
{
    /// <summary>
    /// Stable serializer identifier. The value spells "HIVE" in ASCII (0x48 0x49 0x56 0x45); it must
    /// never change once journals exist, as persisted entries reference it.
    /// </summary>
    public const int SerializerId = 0x48495645;

    public OrgMessageJsonSerializer(ExtendedActorSystem system)
        : base(system)
    {
    }

    public override int Identifier => SerializerId;

    public override string Manifest(object o)
    {
        ArgumentNullException.ThrowIfNull(o);
        return OrgMessageManifests.ForType(o.GetType());
    }

    public override byte[] ToBinary(object obj)
    {
        ArgumentNullException.ThrowIfNull(obj);
        return OrgMessageJsonFormat.Serialize(obj);
    }

    public override object FromBinary(byte[] bytes, string manifest)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        ArgumentException.ThrowIfNullOrEmpty(manifest);
        return OrgMessageJsonFormat.Deserialize(manifest, bytes);
    }
}
