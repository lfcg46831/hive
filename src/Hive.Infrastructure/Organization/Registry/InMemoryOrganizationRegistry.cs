using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Infrastructure.Organization.Registry;

public sealed class InMemoryOrganizationRegistry :
    IOrganizationRegistryReader,
    IOrganizationRegistryStore,
    IOrganizationRelations
{
    private readonly object _gate = new();
    private readonly Dictionary<OrganizationId, OrganizationRegistrySnapshot> _snapshots = new();

    public bool TryGetSnapshot(
        OrganizationId organizationId,
        out OrganizationRegistrySnapshot? snapshot)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        lock (_gate)
        {
            return _snapshots.TryGetValue(organizationId, out snapshot);
        }
    }

    public ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _snapshots.TryGetValue(organizationId, out var snapshot);
            return new ValueTask<OrganizationRegistrySnapshot?>(snapshot);
        }
    }

    ValueTask<OrganizationImportResult> IOrganizationRegistryStore.ApplyAsync(
        OrganizationRegistryProjection target,
        DateTimeOffset importedAt,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();

        lock (_gate)
        {
            _snapshots.TryGetValue(target.OrganizationId, out var current);
            var result = OrganizationRegistryMutation.Apply(current, target, importedAt);
            if (result.Status == OrganizationImportStatus.Applied)
            {
                _snapshots[target.OrganizationId] = result.Snapshot!;
            }

            return new ValueTask<OrganizationImportResult>(result);
        }
    }

    public ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();

        var relations = RequireRelations(organizationId);
        RequirePosition(relations, positionId);
        return new ValueTask<PositionId?>(relations.GetDirectSuperior(positionId));
    }

    public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();

        var relations = RequireRelations(organizationId);
        RequirePosition(relations, positionId);
        return new ValueTask<IReadOnlyCollection<PositionId>>(
            relations.GetDirectSubordinates(positionId));
    }

    public ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<PositionId>(RequireRelations(organizationId).RootUnitLeadership);
    }

    public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<OrganizationOwnerEndpointRef>(RequireRelations(organizationId).Owner);
    }

    public ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();
        return new ValueTask<UnitId?>(RequireRelations(organizationId).GetUnit(positionId));
    }

    private OrganizationRelationsSnapshot RequireRelations(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        lock (_gate)
        {
            if (!_snapshots.TryGetValue(organizationId, out var snapshot))
            {
                throw OrganizationRelationNotFoundException.ForOrganization(organizationId);
            }

            return snapshot.Relations.Value;
        }
    }

    private static void RequirePosition(
        OrganizationRelationsSnapshot relations,
        PositionId positionId)
    {
        if (!relations.ContainsPosition(positionId))
        {
            throw OrganizationRelationNotFoundException.ForPosition(
                relations.OrganizationId,
                positionId);
        }
    }
}
