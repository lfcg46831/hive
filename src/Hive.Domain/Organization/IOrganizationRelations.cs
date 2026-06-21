using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Organization;

/// <summary>
/// Read-only query contract over the materialized organizational structure of a single
/// organization. It exposes exactly the relations that vertical routing and governance
/// validation (US-F0-04) depend on: the direct superior of a position, its direct
/// subordinates, the leadership of the root unit, the configured <c>OrganizationOwner</c>,
/// and the unit a position belongs to.
/// </summary>
/// <remarks>
/// <para>
/// This is a pure query seam: implementations never mutate the registry. The materialized
/// read-only implementation backed by the registry is provided by US-F0-04-T02; this contract
/// only fixes the shape and semantics the validators rely on.
/// </para>
/// <para>
/// Every query is scoped to a single <see cref="OrganizationId"/>, mirroring the envelope of
/// the canonical messages (§9). Implementations must resolve relations only within that
/// organization and must not leak structure across organizations.
/// </para>
/// <para>
/// Unknown organizations or positions are structural errors and are surfaced through
/// <see cref="OrganizationRelationNotFoundException"/>, except where a method documents a
/// <see langword="null"/> return as the existence probe.
/// </para>
/// </remarks>
public interface IOrganizationRelations
{
    /// <summary>
    /// Returns the direct organizational superior of <paramref name="positionId"/>.
    /// </summary>
    /// <returns>
    /// The superior position, or <see langword="null"/> when the position is the leadership of
    /// the root unit and therefore has no direct organizational superior (top of the chain).
    /// </returns>
    /// <exception cref="OrganizationRelationNotFoundException">
    /// The organization or position is not present in the registry.
    /// </exception>
    ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the positions that report directly to <paramref name="positionId"/>.
    /// </summary>
    /// <returns>
    /// The direct subordinates, or an empty collection when the position leads no one. The
    /// collection is never <see langword="null"/> and contains no duplicates.
    /// </returns>
    /// <exception cref="OrganizationRelationNotFoundException">
    /// The organization or position is not present in the registry.
    /// </exception>
    ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the position that leads the root unit of the organization (e.g. the CEO), which
    /// is the single position without a direct organizational superior.
    /// </summary>
    /// <exception cref="OrganizationRelationNotFoundException">
    /// The organization is not present in the registry.
    /// </exception>
    ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the routing endpoint of the configured <c>OrganizationOwner</c>, used as the
    /// destination for escalations raised by the root unit leadership and for the kill switch.
    /// </summary>
    /// <returns>
    /// The owner endpoint. The <c>OrganizationOwner</c> is mandatory per organization, so for a
    /// known organization this is always non-null.
    /// </returns>
    /// <exception cref="OrganizationRelationNotFoundException">
    /// The organization is not present in the registry.
    /// </exception>
    ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns the unit that <paramref name="positionId"/> belongs to.
    /// </summary>
    /// <returns>
    /// The unit of the position, or <see langword="null"/> when the position does not exist in
    /// the organization. This method doubles as the existence probe for a position and never
    /// throws <see cref="OrganizationRelationNotFoundException"/> for an unknown position.
    /// </returns>
    /// <exception cref="OrganizationRelationNotFoundException">
    /// The organization is not present in the registry.
    /// </exception>
    ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default);
}
