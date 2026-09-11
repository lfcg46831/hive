using Hive.Domain.Identity;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class PostgreSqlPeerChannelContracts : IPeerChannelContracts, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RegistryPeerChannelContracts _inner;

    public PostgreSqlPeerChannelContracts(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _inner = new RegistryPeerChannelContracts(new PostgreSqlOrganizationRegistry(_dataSource));
    }

    public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(OrganizationId organizationId,
        UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default) =>
        _inner.ResolveChannelAsync(organizationId, fromUnitId, toUnitId, cancellationToken);

    public void Dispose() => _dataSource.Dispose();
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class UnavailablePeerChannelContracts : IPeerChannelContracts
{
    public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(OrganizationId organizationId,
        UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<PeerChannelConfiguration?>(
            new InvalidOperationException("Peer channel registry is unavailable: PostgreSQL is not configured."));
    }
}
