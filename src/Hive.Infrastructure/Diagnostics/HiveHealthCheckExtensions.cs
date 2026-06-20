using Microsoft.Extensions.DependencyInjection;

namespace Hive.Infrastructure.Diagnostics;

public static class HiveHealthCheckExtensions
{
    /// <summary>
    /// Registers the minimal health checks shared by every host (US-F0-01-T08): process
    /// liveness, configuration loaded, and mandatory dependencies present. Both executables get
    /// the identical set through the common bootstrap so they cannot drift, and each typed check
    /// is resolved from dependency injection. The diagnostic endpoint (US-F0-01-T09) later
    /// exposes them filtered by the <c>live</c> and <c>ready</c> tags.
    /// </summary>
    public static IServiceCollection AddHiveHealthChecks(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services
            .AddHealthChecks()
            .AddCheck<ProcessLivenessHealthCheck>(
                HiveHealthChecks.ProcessName,
                tags: new[] { HiveHealthChecks.LiveTag })
            .AddCheck<ConfigurationHealthCheck>(
                HiveHealthChecks.ConfigurationName,
                tags: new[] { HiveHealthChecks.ReadyTag })
            .AddCheck<RequiredDependenciesHealthCheck>(
                HiveHealthChecks.DependenciesName,
                tags: new[] { HiveHealthChecks.ReadyTag });

        return services;
    }
}
