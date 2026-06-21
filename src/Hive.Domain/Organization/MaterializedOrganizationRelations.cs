using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Organization;

/// <summary>
/// Read-only <see cref="IOrganizationRelations"/> served over the materialized registry
/// (US-F0-04-T02). It resolves relations from one <see cref="OrganizationRelationsSnapshot"/> per
/// organization and never mutates them, mirroring the contract's null/exception semantics exactly.
/// </summary>
/// <remarks>
/// In F0 the snapshots are materialized in memory and supplied at construction; later phases
/// (US-F0-05 GitOps import, PostgreSQL read model) own how they are produced. Because every query
/// is a pure dictionary lookup, the implementation is thread-safe and the returned snapshots are
/// immutable.
/// </remarks>
public sealed class MaterializedOrganizationRelations : IOrganizationRelations
{
    private readonly IReadOnlyDictionary<OrganizationId, OrganizationRelationsSnapshot> _snapshots;

    /// <summary>
    /// Creates a service over the supplied materialized organization snapshots.
    /// </summary>
    /// <exception cref="ArgumentException">Two snapshots describe the same organization.</exception>
    public MaterializedOrganizationRelations(IEnumerable<OrganizationRelationsSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);

        var byOrganization = new Dictionary<OrganizationId, OrganizationRelationsSnapshot>();
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);

            if (!byOrganization.TryAdd(snapshot.OrganizationId, snapshot))
            {
                throw new ArgumentException(
                    $"More than one snapshot was supplied for organization '{snapshot.OrganizationId.Value}'.",
                    nameof(snapshots));
            }
        }

        _snapshots = byOrganization;
    }

    /// <summary>Creates a service over a single organization snapshot.</summary>
    public MaterializedOrganizationRelations(OrganizationRelationsSnapshot snapshot)
        : this(new[] { snapshot ?? throw new ArgumentNullException(nameof(snapshot)) })
    {
    }

    public ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        var snapshot = RequireOrganization(organizationId);
        RequirePosition(snapshot, positionId);
        return new ValueTask<PositionId?>(snapshot.GetDirectSuperior(positionId));
    }

    public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        var snapshot = RequireOrganization(organizationId);
        RequirePosition(snapshot, positionId);
        return new ValueTask<IReadOnlyCollection<PositionId>>(snapshot.GetDirectSubordinates(positionId));
    }

    public ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        var snapshot = RequireOrganization(organizationId);
        return new ValueTask<PositionId>(snapshot.RootUnitLeadership);
    }

    public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        var snapshot = RequireOrganization(organizationId);
        return new ValueTask<OrganizationOwnerEndpointRef>(snapshot.Owner);
    }

    public ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        var snapshot = RequireOrganization(organizationId);
        return new ValueTask<UnitId?>(snapshot.GetUnit(positionId));
    }

    private OrganizationRelationsSnapshot RequireOrganization(OrganizationId organizationId)
    {
        if (!_snapshots.TryGetValue(organizationId, out var snapshot))
        {
            throw OrganizationRelationNotFoundException.ForOrganization(organizationId);
        }

        return snapshot;
    }

    private static void RequirePosition(OrganizationRelationsSnapshot snapshot, PositionId positionId)
    {
        if (!snapshot.ContainsPosition(positionId))
        {
            throw OrganizationRelationNotFoundException.ForPosition(snapshot.OrganizationId, positionId);
        }
    }
}
