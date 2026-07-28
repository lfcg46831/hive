using Hive.Domain.Identity;
using Hive.Domain.Outcomes;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class PostgreSqlOutcomePolicyProvider :
    IOutcomePolicyProvider,
    IDisposable,
    IAsyncDisposable
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly RegistryOutcomePolicyProvider _inner;

    public PostgreSqlOutcomePolicyProvider(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException(
                "PostgreSQL connection string is required.",
                nameof(connectionString));
        }

        _dataSource = NpgsqlDataSource.Create(connectionString);
        _inner = new RegistryOutcomePolicyProvider(
            new PostgreSqlOrganizationRegistry(_dataSource));
    }

    public ValueTask<OutcomePolicySnapshot> GetPolicyAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        _inner.GetPolicyAsync(organizationId, positionId, cancellationToken);

    public void Dispose() => _dataSource.Dispose();

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();
}
