using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Readiness probe for configuration: the typed <c>Hive</c> options bound and at least one valid
/// node role is active. Fail-fast validation (US-F0-01-T05) already rejects unknown, empty or
/// duplicate roles when the host starts; this confirms the loaded configuration at runtime and
/// guards against an empty active-role set. Tagged <see cref="HiveHealthChecks.ReadyTag"/>.
/// </summary>
public sealed class ConfigurationHealthCheck : IHealthCheck
{
    private readonly ActiveNodeRoles _activeRoles;

    public ConfigurationHealthCheck(ActiveNodeRoles activeRoles)
    {
        _activeRoles = activeRoles ?? throw new ArgumentNullException(nameof(activeRoles));
    }

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        if (_activeRoles.Values.Count == 0)
        {
            return Task.FromResult(HealthCheckResult.Unhealthy(
                "Configuration not loaded: no node roles are active (Hive:Node:Roles is empty)."));
        }

        var roles = string.Join(", ", _activeRoles.Values);
        return Task.FromResult(HealthCheckResult.Healthy(
            $"Configuration loaded; active roles: {roles}."));
    }
}
