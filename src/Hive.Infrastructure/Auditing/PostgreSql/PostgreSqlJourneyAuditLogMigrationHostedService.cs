using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.Auditing.PostgreSql;

internal sealed class PostgreSqlJourneyAuditLogMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<PostgreSqlJourneyAuditLogMigrationHostedService> _logger;

    public PostgreSqlJourneyAuditLogMigrationHostedService(
        IConfiguration configuration,
        ILogger<PostgreSqlJourneyAuditLogMigrationHostedService> logger)
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
                "Skipping journey audit log migrations because connection string {ConnectionStringName} is not configured.",
                ConnectionStringNames.PostgreSql);
            return;
        }

        _logger.LogInformation("Applying journey audit log PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Journey audit log PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
