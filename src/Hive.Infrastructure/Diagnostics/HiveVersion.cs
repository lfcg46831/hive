using System.Reflection;

namespace Hive.Infrastructure.Diagnostics;

/// <summary>
/// Resolves the running host's version for the diagnostic endpoint (US-F0-01-T09). Prefers the
/// <see cref="AssemblyInformationalVersionAttribute"/> (the SemVer the build stamps) and strips any
/// <c>+sourceRevision</c> suffix so the exposed value stays stable across rebuilds of the same
/// version. Falls back to the assembly version, and to <c>"unknown"</c> when no entry assembly is
/// available (for example, when hosted outside a normal process entry point).
/// </summary>
public static class HiveVersion
{
    /// <summary>Version of the current process entry assembly, computed once.</summary>
    public static string Current { get; } = Resolve(Assembly.GetEntryAssembly());

    /// <summary>Resolves the displayable version of a specific assembly. Exposed for testing.</summary>
    public static string Resolve(Assembly? assembly)
    {
        if (assembly is null)
        {
            return "unknown";
        }

        var informational = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;

        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+');
            return plus >= 0 ? informational[..plus] : informational;
        }

        return assembly.GetName().Version?.ToString() ?? "unknown";
    }
}
