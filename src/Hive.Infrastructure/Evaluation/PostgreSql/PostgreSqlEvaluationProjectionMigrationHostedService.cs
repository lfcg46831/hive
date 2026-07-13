using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Infrastructure.Evaluation.PostgreSql;

internal sealed class PostgreSqlEvaluationProjectionMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly EvaluationProfileCatalog _catalog;
    private readonly ILogger<PostgreSqlEvaluationProjectionMigrationHostedService> _logger;

    public PostgreSqlEvaluationProjectionMigrationHostedService(
        IConfiguration configuration,
        EvaluationProfileCatalog catalog,
        ILogger<PostgreSqlEvaluationProjectionMigrationHostedService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (_catalog.Count == 0)
        {
            return;
        }

        var connectionString = _configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "An enabled evaluation profile requires ConnectionStrings:PostgreSql for projection readiness.");
        }

        _logger.LogInformation("Applying evaluation projection PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlEvaluationProjectionMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        _logger.LogInformation("Evaluation projection PostgreSQL migrations are current.");
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
