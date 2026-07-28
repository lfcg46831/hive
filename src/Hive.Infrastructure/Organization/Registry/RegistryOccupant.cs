using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;

namespace Hive.Infrastructure.Organization.Registry;

public sealed record RegistryOccupant(
    PositionId PositionId,
    OccupantType Type,
    string? IdentityPromptRef,
    AiConfiguration? Ai,
    WorkingHoursConfiguration? WorkingHours,
    IReadOnlyList<SubscriptionConfiguration> Subscriptions,
    IReadOnlyList<ToolConfiguration> Tools,
    OutcomePolicyOverlay? OutcomePolicy);
