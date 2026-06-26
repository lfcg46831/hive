using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Persistence.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Readiness probe for the Akka.Persistence journal/snapshot store of the position subsystem
/// (US-F0-06-T05a). The persistent <c>PositionActor</c> cannot recover inbox, tasks, short memory or
/// history without it, so a node is not ready to host positions until persistence is configured.
/// Like <see cref="RequiredDependenciesHealthCheck"/> this verifies configuration rather than opening
/// a connection: the dedicated schema and tables are guaranteed by the persistence migration that
/// runs in the common bootstrap and aborts host startup if it cannot apply, so probing here would
/// only duplicate that guarantee while making readiness flap on transient database availability.
/// Reports the dedicated schema in its description so the persistence layout stays observable. Tagged
/// <see cref="HiveHealthChecks.ReadyTag"/>.
/// </summary>
public sealed class PositionPersistenceHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public PositionPersistenceHealthCheck(IConfiguration configuration)
    {
        _configuration = configuration ?? throw new ArgumentNullException(nameof(configuration));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var connectionString = _configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                $"Position persistence is not configured: connection string '{ConnectionStringNames.PostgreSql}' is not set."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            $"Position persistence is configured (journal/snapshot schema '{PositionPersistenceSchema.SchemaName}')."));
    }
}
