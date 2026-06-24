using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryOccupant(
    PositionId PositionId,
    OccupantType Type,
    string? IdentityPromptRef,
    AiConfiguration? Ai,
    WorkingHoursConfiguration? WorkingHours,
    IReadOnlyList<SubscriptionConfiguration> Subscriptions,
    IReadOnlyList<ToolConfiguration> Tools);
