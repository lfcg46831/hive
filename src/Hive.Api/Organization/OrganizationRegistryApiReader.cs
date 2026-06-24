using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Api.Organization;

internal sealed class OrganizationRegistryApiReader : IAsyncDisposable
{
    private readonly NpgsqlDataSource? _dataSource;

    public OrganizationRegistryApiReader(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return;
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        Reader = new PostgreSqlOrganizationRegistry(_dataSource);
    }

    public IOrganizationRegistryReader? Reader { get; }

    public ValueTask DisposeAsync() =>
        _dataSource is null ? ValueTask.CompletedTask : _dataSource.DisposeAsync();
}
