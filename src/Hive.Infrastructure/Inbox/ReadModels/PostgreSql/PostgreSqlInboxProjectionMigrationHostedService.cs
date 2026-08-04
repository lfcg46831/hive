using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

internal sealed class PostgreSqlInboxProjectionMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgreSqlInboxProjectionMigrationHostedService> _logger;

    public PostgreSqlInboxProjectionMigrationHostedService(
        IConfiguration configuration,
        ILogger<PostgreSqlInboxProjectionMigrationHostedService> logger)
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
                "Skipping inbox projection migrations because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        _logger.LogInformation("Applying inbox projection PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlInboxProjectionMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Inbox projection PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
