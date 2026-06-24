using Akka.Cluster.Sharding;
using Hive.Domain.Identity;

namespace Hive.Actors.Sharding;

/// <summary>
/// The Cluster Sharding message extractor and shard resolver for the <c>PositionActor</c>
/// (US-F0-06-T04a). It maps a <see cref="PositionEnvelope"/> to the sharded <c>entityId</c>
/// (<see cref="PositionEntityId.Value"/>, the canonical <c>OrganizationId/PositionId</c> form), the
/// shard id, and the unwrapped <see cref="PositionEnvelope.Command"/> handed to the entity.
/// </summary>
/// <remarks>
/// <para>
/// Shard resolution derives from the entity type name (<see cref="PositionEntityId.EntityTypeName"/>)
/// and reuses Akka's <see cref="HashCodeMessageExtractor"/>: the shard id is a stable hash of the
/// entity id modulo <see cref="HashCodeMessageExtractor.MaxNumberOfShards"/>. The shard count is a
/// long-lived placement contract — it must be identical on every node and must not change while
/// entities are persisted, because changing it reshuffles every position across shards. Hosts pin it
/// from configuration in US-F0-06-T04b; <see cref="DefaultNumberOfShards"/> is the stable default.
/// </para>
/// <para>
/// Only <see cref="PositionEnvelope"/> messages are routable: <see cref="EntityId"/> returns
/// <see langword="null"/> for anything else, so unaddressed messages are dropped rather than sent to
/// an arbitrary entity. This keeps the addressing contract explicit — every command is delivered by
/// wrapping it in an envelope (US-F0-06-T02).
/// </para>
/// </remarks>
public sealed class PositionMessageExtractor : HashCodeMessageExtractor
{
    /// <summary>
    /// The default, stable number of shards for the position entity type. Chosen well above the F0
    /// node count (1–3 nodes) to spread positions evenly while leaving headroom; it is part of the
    /// placement contract and must not change once entities are persisted.
    /// </summary>
    public const int DefaultNumberOfShards = 50;

    public PositionMessageExtractor()
        : this(DefaultNumberOfShards)
    {
    }

    public PositionMessageExtractor(int numberOfShards)
        : base(RequirePositive(numberOfShards))
    {
    }

    /// <summary>
    /// Extracts the canonical entity id from an envelope. Returns <see langword="null"/> for any
    /// message that is not a <see cref="PositionEnvelope"/>, marking it as not routable.
    /// </summary>
    public override string? EntityId(object message) =>
        message is PositionEnvelope envelope ? envelope.Position.Value : null;

    /// <summary>
    /// Unwraps the address-free command an entity actually handles; non-envelope messages pass
    /// through unchanged for Akka's internal handling.
    /// </summary>
    public override object EntityMessage(object message) =>
        message is PositionEnvelope envelope ? envelope.Command : message;

    private static int RequirePositive(int numberOfShards)
    {
        if (numberOfShards <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberOfShards),
                numberOfShards,
                "The number of shards must be greater than zero.");
        }

        return numberOfShards;
    }
}
