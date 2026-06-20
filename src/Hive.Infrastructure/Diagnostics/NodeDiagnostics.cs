namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Minimal diagnostic snapshot a host exposes (US-F0-01-T09): the running version, the active
/// node roles, and the startup state expressed through the <c>live</c>/<c>ready</c> health-check
/// tags defined in §11.1. <see cref="Live"/> answers "is the process up?" and <see cref="Ready"/>
/// answers "can the node serve work?" (mandatory configuration and dependencies present). Kept as
/// an immutable record so it is safe to serialize and cannot drift after it is built.
/// </summary>
public sealed record NodeDiagnostics
{
    /// <summary>Displayable version of the running host (see <see cref="HiveVersion"/>).</summary>
    public required string Version { get; init; }

    /// <summary>Canonical roles active on this node (<c>Hive:Node:Roles</c>).</summary>
    public required IReadOnlyCollection<string> Roles { get; init; }

    /// <summary>True when every <c>live</c>-tagged health check is healthy.</summary>
    public required bool Live { get; init; }

    /// <summary>True when every <c>ready</c>-tagged health check is healthy.</summary>
    public required bool Ready { get; init; }
}
