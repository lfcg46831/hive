using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Readiness probe for mandatory dependencies: every external dependency the node needs to do
/// real work must be configured before it reports ready. In F0 the single mandatory dependency
/// is the PostgreSQL connection string (ADR-003), which is declared empty in tracked config and
/// supplied per environment, so an empty value means the node is not ready. Later stories extend
/// this check as new mandatory dependencies appear. Tagged <see cref="HiveHealthChecks.ReadyTag"/>.
/// </summary>
public sealed class RequiredDependenciesHealthCheck : IHealthCheck
{
    private readonly IConfiguration _configuration;

    public RequiredDependenciesHealthCheck(IConfiguration configuration)
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
                $"Mandatory dependency missing: connection string '{ConnectionStringNames.PostgreSql}' is not configured."));
        }

        return Task.FromResult(HealthCheckResult.Healthy(
            "All mandatory dependencies are configured."));
    }
}
