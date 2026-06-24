namespace Hive.Api.Organization;

public sealed record OrganizationResponse(
    string Id,
    string? Name,
    string RootUnitId,
    OwnerResponse Owner,
    IReadOnlyList<PromptResponse> Prompts,
    long Version,
    string Fingerprint,
    DateTimeOffset ImportedAt,
    DateTimeOffset UpdatedAt);

public sealed record OwnerResponse(string Type, string Ref);

public sealed record PromptResponse(string Id, string Path);

public sealed record UnitsResponse(IReadOnlyList<UnitResponse> Units);

public sealed record UnitResponse(
    string Id,
    string? Name,
    string? ParentId,
    string LeadershipPositionId,
    DateTimeOffset UpdatedAt);

public sealed record PositionsResponse(IReadOnlyList<PositionResponse> Positions);

public sealed record PositionResponse(
    string Id,
    string? Name,
    string UnitId,
    string? ReportsToPositionId,
    string? Timezone,
    DateTimeOffset UpdatedAt);

public sealed record CommandRelationsResponse(
    OwnerResponse Owner,
    string RootUnitLeadershipPositionId,
    IReadOnlyList<CommandRelationResponse> Relations);

public sealed record CommandRelationResponse(
    string PositionId,
    string UnitId,
    string? ReportsToPositionId,
    IReadOnlyList<string> DirectSubordinatePositionIds);

public sealed record PositionConfigurationResponse(
    PositionResponse Position,
    OccupantResponse Occupant,
    AuthorityResponse Authority,
    IReadOnlyList<ScheduleResponse> Schedules);

public sealed record OccupantResponse(
    string Type,
    string? IdentityPromptRef,
    AiResponse? Ai,
    WorkingHoursResponse? WorkingHours,
    IReadOnlyList<SubscriptionResponse> Subscriptions,
    IReadOnlyList<ToolResponse> Tools,
    DateTimeOffset UpdatedAt);

public sealed record AiResponse(
    string Provider,
    string Model,
    double? Temperature,
    int? MaxTokens,
    string? Processing,
    string? BatchWindow,
    IReadOnlyList<AiFallbackResponse> Fallback,
    BudgetResponse? Budget);

public sealed record AiFallbackResponse(string Provider, string Model);

public sealed record BudgetResponse(
    decimal? ReactiveMaxEurPerDay,
    decimal? ProactiveMaxEurPerDay,
    decimal? TotalMaxEurPerDay,
    int? MaxCallsPerHour);

public sealed record WorkingHoursResponse(string Start, string End);

public sealed record SubscriptionResponse(string Event, string Within);

public sealed record ToolResponse(string Connector, IReadOnlyList<string> Scope);

public sealed record AuthorityResponse(
    IReadOnlyList<string> CanDecide,
    IReadOnlyList<string> MustEscalate,
    IReadOnlyList<string> RequiresHumanApproval,
    DateTimeOffset UpdatedAt);

public sealed record ScheduleResponse(
    string Id,
    string Cron,
    string Instruction,
    DateTimeOffset UpdatedAt);
