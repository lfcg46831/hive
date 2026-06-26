namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Names and tags for the minimal health checks every host registers (US-F0-01-T08).
/// Liveness answers "is the process alive?" and readiness answers "is this node ready to do
/// work?". The diagnostic endpoint (US-F0-01-T09) selects checks by these tags, so the names
/// and tags are part of the host's observable contract and live in one place.
/// </summary>
public static class HiveHealthChecks
{
    /// <summary>Liveness check: the process is alive and its scheduler is responsive.</summary>
    public const string ProcessName = "process";

    /// <summary>Readiness check: the typed <c>Hive</c> configuration is loaded with active roles.</summary>
    public const string ConfigurationName = "configuration";

    /// <summary>Readiness check: every mandatory external dependency is configured.</summary>
    public const string DependenciesName = "dependencies";

    /// <summary>Readiness check: the position journal/snapshot persistence is configured.</summary>
    public const string PersistenceName = "persistence";

    /// <summary>Tag for liveness checks ("is the process up?").</summary>
    public const string LiveTag = "live";

    /// <summary>Tag for readiness checks ("can the node serve work?").</summary>
    public const string ReadyTag = "ready";
}
