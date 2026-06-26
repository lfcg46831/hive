using Akka.Actor;

namespace Hive.Actors.Sharding;

/// <summary>
/// Seam that supplies the Akka <see cref="Props"/> Cluster Sharding uses to spawn a position
/// entity for a given <c>entityId</c> (the canonical <c>OrganizationId/PositionId</c> form).
/// </summary>
/// <remarks>
/// US-F0-06-T04b owns the shard-region initialization and depends only on this seam, never on a
/// concrete entity. US-F0-06-T06b supplies the default persistent <c>PositionActor</c> props through
/// this boundary, so later entity behaviour can evolve without touching the sharding wiring of T04b.
/// </remarks>
public interface IPositionEntityProps
{
    /// <summary>Creates the <see cref="Props"/> for the position entity with the given id.</summary>
    Props Create(string entityId);
}
