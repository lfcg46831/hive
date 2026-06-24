using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryPosition(
    PositionId Id,
    string? Name,
    UnitId Unit,
    PositionId? ReportsTo,
    string? Timezone);
