using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.Scheduling.PostgreSql;

internal sealed class PostgreSqlSchedulerPulseDeliveryMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgreSqlSchedulerPulseDeliveryMigrationHostedService> _logger;

    public PostgreSqlSchedulerPulseDeliveryMigrationHostedService(
        IConfiguration configuration,
        ILogger<PostgreSqlSchedulerPulseDeliveryMigrationHostedService> logger)
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
                "Skipping scheduler pulse delivery migrations because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        _logger.LogInformation("Applying scheduler pulse delivery PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlSchedulerPulseDeliveryMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Scheduler pulse delivery PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
