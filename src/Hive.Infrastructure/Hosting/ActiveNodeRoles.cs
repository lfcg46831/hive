using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Hosting;

/// <summary>
/// The validated, normalized set of roles this node declares. Raw values from
/// <c>Hive:Node:Roles</c> are trimmed and canonicalized to <see cref="NodeRoleNames"/>.
/// Validation (US-F0-01-T05) has already rejected unknown, empty or duplicate roles by the
/// time this type is resolved, so normalization here is safe and lossless.
/// </summary>
public sealed class ActiveNodeRoles
{
    private readonly HashSet<string> _roles;

    public ActiveNodeRoles(IOptions<HiveOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _roles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var role in options.Value.Node?.Roles ?? [])
        {
            if (string.IsNullOrWhiteSpace(role))
            {
                continue;
            }

            var trimmed = role.Trim();
            var canonical = NodeRoleNames.All
                .FirstOrDefault(known => string.Equals(known, trimmed, StringComparison.OrdinalIgnoreCase));
            _roles.Add(canonical ?? trimmed);
        }
    }

    /// <summary>Canonical, lowercase role names active on this node.</summary>
    public IReadOnlyCollection<string> Values => _roles;

    public bool Contains(string role) => role is not null && _roles.Contains(role);
}
