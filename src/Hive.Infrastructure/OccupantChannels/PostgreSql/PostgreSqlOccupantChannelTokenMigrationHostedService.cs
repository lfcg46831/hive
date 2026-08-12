using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.OccupantChannels.PostgreSql;

internal sealed class PostgreSqlOccupantChannelTokenMigrationHostedService(
    IConfiguration configuration,
    ILogger<PostgreSqlOccupantChannelTokenMigrationHostedService> logger)
    : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            logger.LogWarning(
                "Skipping occupant-channel token migrations because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        logger.LogInformation("Applying occupant-channel token PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlOccupantChannelTokenMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("Occupant-channel token PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
