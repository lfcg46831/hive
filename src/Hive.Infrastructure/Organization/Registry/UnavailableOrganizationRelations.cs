using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class UnavailableOrganizationRelations(string connectionStringName) : IOrganizationRelations
{
    public ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        Unavailable<PositionId?>();

    public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        Unavailable<IReadOnlyCollection<PositionId>>();

    public ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        Unavailable<PositionId>();

    public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        Unavailable<OrganizationOwnerEndpointRef>();

    public ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        Unavailable<UnitId?>();

    private ValueTask<T> Unavailable<T>() =>
        ValueTask.FromException<T>(new InvalidOperationException(
            $"Organization relations are unavailable because connection string '{connectionStringName}' is not configured."));
}
