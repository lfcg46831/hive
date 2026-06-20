using Hive.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace Hive.Api.Diagnostics;

/// <summary>
/// Maps the minimal diagnostic surface for the API host (US-F0-01-T09). It exposes the two health
/// probes selected by the §11.1 tags — <c>/health/live</c> (liveness) and <c>/health/ready</c>
/// (readiness, gated on mandatory configuration) — plus a <c>/diagnostics</c> document with the
/// version, active roles and startup state. The probe routes return the standard 200/503 status so
/// orchestration (US-F0-02 Docker health checks) can consume them directly.
/// </summary>
public static class DiagnosticsEndpointExtensions
{
    public const string LivePath = "/health/live";
    public const string ReadyPath = "/health/ready";
    public const string DiagnosticsPath = "/diagnostics";

    public static IEndpointRouteBuilder MapHiveDiagnostics(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapHealthChecks(LivePath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HiveHealthChecks.LiveTag),
        });

        endpoints.MapHealthChecks(ReadyPath, new HealthCheckOptions
        {
            Predicate = registration => registration.Tags.Contains(HiveHealthChecks.ReadyTag),
        });

        endpoints.MapGet(DiagnosticsPath, async (
            NodeDiagnosticsProvider diagnostics,
            CancellationToken cancellationToken) =>
            Results.Json(await diagnostics.GetAsync(cancellationToken)));

        return endpoints;
    }
}
