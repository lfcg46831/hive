using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Liveness probe: if the host can execute this check, the process is alive and its scheduler
/// is responsive, so it is healthy by construction. A dead or deadlocked process is signalled
/// by the check never running rather than by returning unhealthy. Tagged
/// <see cref="HiveHealthChecks.LiveTag"/> so the diagnostic endpoint (US-F0-01-T09) can expose
/// liveness independently of readiness.
/// </summary>
public sealed class ProcessLivenessHealthCheck : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(HealthCheckResult.Healthy("Process is alive."));
}
