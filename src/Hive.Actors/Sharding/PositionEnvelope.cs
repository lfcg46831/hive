using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Actors.Sharding;

/// <summary>
/// The sharded-message envelope for the <c>PositionActor</c> (US-F0-06-T04a): the unit of delivery
/// that Cluster Sharding routes to a position entity. It pairs the destination
/// <see cref="PositionEntityId"/> with the <see cref="PositionCommand"/> payload, so the transport
/// layer carries the addressing while the command stays a pure, address-free domain intent
/// (US-F0-06-T02).
/// </summary>
/// <remarks>
/// <para>
/// Senders always wrap a command in an envelope before tell-ing the shard region; the
/// <see cref="PositionMessageExtractor"/> reads <see cref="Position"/> to derive the entity id and
/// shard id and unwraps <see cref="Command"/> as the message handed to the entity. The entity
/// therefore receives the bare command and never the envelope.
/// </para>
/// <para>
/// <strong>Serialization convention (US-F0-06-T04a).</strong> The envelope is a sharded/remote
/// message and follows the protocol-wide ADR-007 rules: it is serialized with the versionable
/// System.Text.Json format under a stable string manifest, never Akka's default .NET serializer, and
/// no CLR type names leak onto the wire. <see cref="Position"/> serializes as the canonical
/// <see cref="PositionEntityId.Value"/> textual form and is read back through
/// <see cref="PositionEntityId.Parse(string)"/> (tolerant read, no silent defaults, invalid values
/// rejected); <see cref="Command"/> reuses the closed <see cref="PositionCommand"/> manifests.
/// Binding the concrete serializers to these types is US-F0-06-T05b; this contract only fixes the
/// shape and the rules.
/// </para>
/// </remarks>
public sealed record PositionEnvelope
{
    public PositionEnvelope(PositionEntityId position, PositionCommand command)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(command);

        Position = position;
        Command = command;
    }

    /// <summary>The destination position entity; supplies the sharded <c>entityId</c> and shard id.</summary>
    public PositionEntityId Position { get; }

    /// <summary>The address-free command delivered to the entity once the envelope is unwrapped.</summary>
    public PositionCommand Command { get; }

    /// <summary>Convenience factory mirroring the constructor for fluent call sites.</summary>
    public static PositionEnvelope For(PositionEntityId position, PositionCommand command) =>
        new(position, command);
}
