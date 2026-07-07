using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class PostgreSqlOrganizationRelations :
    IOrganizationRelations,
    IDisposable,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgreSqlOrganizationRegistry _inner;

    public PostgreSqlOrganizationRelations(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _inner = new PostgreSqlOrganizationRegistry(_dataSource);
    }

    public ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        _inner.GetDirectSuperiorAsync(organizationId, positionId, cancellationToken);

    public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        _inner.GetDirectSubordinatesAsync(organizationId, positionId, cancellationToken);

    public ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        _inner.GetRootUnitLeadershipAsync(organizationId, cancellationToken);

    public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        _inner.GetOrganizationOwnerAsync(organizationId, cancellationToken);

    public ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        _inner.GetUnitOfPositionAsync(organizationId, positionId, cancellationToken);

    public void Dispose() => _dataSource.Dispose();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
