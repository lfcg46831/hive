using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.Persistence.PostgreSql;

/// <summary>
/// Applies the position-subsystem Akka.Persistence schema migrations during the common host
/// bootstrap (US-F0-06-T05a), guaranteeing the dedicated persistence schema exists before role
/// workloads start the persistent <c>PositionActor</c> via Cluster Sharding and before the plugin
/// auto-initializes its journal/snapshot tables inside that schema. It mirrors the organization
/// registry migration hosted service: when <c>ConnectionStrings:PostgreSql</c> is absent it skips
/// without connecting, preserving the not-ready state; when configured a failed connection or
/// migration aborts host startup.
/// </summary>
internal sealed class PostgreSqlPositionPersistenceMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgreSqlPositionPersistenceMigrationHostedService> _logger;

    public PostgreSqlPositionPersistenceMigrationHostedService(
        IConfiguration configuration,
        ILogger<PostgreSqlPositionPersistenceMigrationHostedService> logger)
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
                "Skipping position persistence migrations because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        _logger.LogInformation("Applying position persistence PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlPositionPersistenceMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Position persistence PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
