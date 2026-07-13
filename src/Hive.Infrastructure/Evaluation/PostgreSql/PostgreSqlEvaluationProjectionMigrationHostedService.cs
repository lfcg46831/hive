using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Hive.Infrastructure.Evaluation.PostgreSql;

internal sealed class PostgreSqlEvaluationProjectionMigrationHostedService : IHostedService
{
    private readonly IConfiguration _configuration;
    private readonly IOptions<EvaluationProjectionOptions> _options;
    private readonly ILogger<PostgreSqlEvaluationProjectionMigrationHostedService> _logger;

    public PostgreSqlEvaluationProjectionMigrationHostedService(
        IConfiguration configuration,
        IOptions<EvaluationProjectionOptions> options,
        ILogger<PostgreSqlEvaluationProjectionMigrationHostedService> logger)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_options.Value.Enabled)
        {
            return;
        }

        var connectionString = _configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                "Evaluation projection requires ConnectionStrings:PostgreSql when enabled.");
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
