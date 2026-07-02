using Hive.Domain.Identity;
using Hive.Domain.Positions;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class PostgreSqlPositionConfigurationProvider :
    IPositionConfigurationProvider,
    IDisposable,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RegistryPositionConfigurationProvider _inner;

    public PostgreSqlPositionConfigurationProvider(string connectionString)
        : this(connectionString, Path.Combine("config", "organizations"))
    {
    }

    public PostgreSqlPositionConfigurationProvider(
        string connectionString,
        string organizationsRoot)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required.", nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _inner = new RegistryPositionConfigurationProvider(
            new PostgreSqlOrganizationRegistry(_dataSource),
            organizationsRoot);
    }

    public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken) =>
        _inner.LoadAsync(entityId, cancellationToken);

    public void Dispose() => _dataSource.Dispose();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
