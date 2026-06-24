using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.Organization.Registry.PostgreSql;

internal sealed class PostgreSqlOrganizationRegistryMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgreSqlOrganizationRegistryMigrationHostedService> _logger;

    public PostgreSqlOrganizationRegistryMigrationHostedService(
        IConfiguration configuration,
        ILogger<PostgreSqlOrganizationRegistryMigrationHostedService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = _configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogWarning(
                "Skipping organization registry migrations because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        _logger.LogInformation("Applying organization registry PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlOrganizationRegistryMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Organization registry PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
