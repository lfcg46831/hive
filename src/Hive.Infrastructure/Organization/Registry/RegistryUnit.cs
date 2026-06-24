using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryUnit(
    UnitId Id,
    string? Name,
    UnitId? Parent,
    PositionId Leadership);
