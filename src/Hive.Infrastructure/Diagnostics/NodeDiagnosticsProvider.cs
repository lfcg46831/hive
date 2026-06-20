using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Builds the <see cref="NodeDiagnostics"/> snapshot for the diagnostic endpoint (US-F0-01-T09).
/// Roles come from the validated <see cref="ActiveNodeRoles"/>; the startup state is derived from a
/// single health-check run rolled up per tag, so it reuses the same checks registered in
/// US-F0-01-T08 instead of re-implementing readiness. A tag is reported healthy only when at least
/// one check carries it and every such check is healthy, matching the §11.1 contract where missing
/// mandatory configuration keeps the node deliberately not-ready.
/// </summary>
public sealed class NodeDiagnosticsProvider
{
    private readonly ActiveNodeRoles _activeRoles;
    private readonly HealthCheckService _healthChecks;
    private readonly string _version;

    public NodeDiagnosticsProvider(ActiveNodeRoles activeRoles, HealthCheckService healthChecks)
        : this(activeRoles, healthChecks, HiveVersion.Current)
    {
    }

    private NodeDiagnosticsProvider(
        ActiveNodeRoles activeRoles,
        HealthCheckService healthChecks,
        string version)
    {
        _activeRoles = activeRoles ?? throw new ArgumentNullException(nameof(activeRoles));
        _healthChecks = healthChecks ?? throw new ArgumentNullException(nameof(healthChecks));
        _version = version ?? throw new ArgumentNullException(nameof(version));
    }

    public async Task<NodeDiagnostics> GetAsync(CancellationToken cancellationToken = default)
    {
        var report = await _healthChecks.CheckHealthAsync(cancellationToken).ConfigureAwait(false);

        return new NodeDiagnostics
        {
            Version = _version,
            Roles = _activeRoles.Values.OrderBy(role => role, StringComparer.Ordinal).ToArray(),
            Live = IsTagHealthy(report, HiveHealthChecks.LiveTag),
            Ready = IsTagHealthy(report, HiveHealthChecks.ReadyTag),
        };
    }

    private static bool IsTagHealthy(HealthReport report, string tag)
    {
        var tagged = report.Entries.Values.Where(entry => entry.Tags.Contains(tag)).ToArray();
        return tagged.Length > 0 && tagged.All(entry => entry.Status == HealthStatus.Healthy);
    }
}
