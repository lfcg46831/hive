using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class PostgreSqlEventSubscriptions : IEventSubscriptions, IDisposable, IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RegistryEventSubscriptions _inner;

    public PostgreSqlEventSubscriptions(string connectionString)
    {
        _dataSource = NpgsqlDataSource.Create(connectionString);
        _inner = new RegistryEventSubscriptions(new PostgreSqlOrganizationRegistry(_dataSource));
    }

    public ValueTask<EventSubscriptionsSnapshot> GetSnapshotAsync(OrganizationId organizationId,
        CancellationToken cancellationToken = default) => _inner.GetSnapshotAsync(organizationId, cancellationToken);

    public void Dispose() => _dataSource.Dispose();
    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}

internal sealed class UnavailableEventSubscriptions : IEventSubscriptions
{
    public ValueTask<EventSubscriptionsSnapshot> GetSnapshotAsync(OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromException<EventSubscriptionsSnapshot>(
            new InvalidOperationException("Event subscriptions registry is unavailable: PostgreSQL is not configured."));
    }
}
