using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryAuthority(
    PositionId PositionId,
    IReadOnlyList<string> CanDecide,
    IReadOnlyList<string> MustEscalate,
    IReadOnlyList<string> RequiresHumanApproval);
