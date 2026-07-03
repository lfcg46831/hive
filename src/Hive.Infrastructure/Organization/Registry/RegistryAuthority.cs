using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryAuthority(
    PositionId PositionId,
    IReadOnlyList<string> CanDecide,
    IReadOnlyList<RegistryAuthorityOverride> Overrides);

public sealed record RegistryAuthorityOverride(
    string Key,
    ActionDomainGate Gate,
    string? Approver);
