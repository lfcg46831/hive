using Hive.Domain.Organization.Configuration;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Api.Organization;

internal static class OrganizationRegistryResponseMapper
{
    public static OrganizationResponse MapOrganization(OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var organization = snapshot.Organization;
        return new OrganizationResponse(
            organization.Value.Id.Value,
            organization.Value.Name,
            organization.Value.RootUnit.Value,
            MapOwner(organization.Value.Owner),
            organization.Value.Prompts
                .OrderBy(prompt => prompt.Id, StringComparer.Ordinal)
                .Select(prompt => new PromptResponse(prompt.Id, prompt.Path))
                .ToArray(),
            snapshot.Version,
            snapshot.Fingerprint,
            snapshot.ImportedAt,
            organization.UpdatedAt);
    }

    public static UnitsResponse MapUnits(OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new UnitsResponse(
            snapshot.Units.Values
                .OrderBy(entry => entry.Value.Id.Value, StringComparer.Ordinal)
                .Select(entry => new UnitResponse(
                    entry.Value.Id.Value,
                    entry.Value.Name,
                    entry.Value.Parent?.Value,
                    entry.Value.Leadership.Value,
                    entry.UpdatedAt))
                .ToArray());
    }

    public static PositionsResponse MapPositions(OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new PositionsResponse(
            snapshot.Positions.Values
                .OrderBy(entry => entry.Value.Id.Value, StringComparer.Ordinal)
                .Select(MapPosition)
                .ToArray());
    }

    public static CommandRelationsResponse MapCommandRelations(
        OrganizationRegistrySnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new CommandRelationsResponse(
            MapOwner(snapshot.Organization.Value.Owner),
            snapshot.Relations.Value.RootUnitLeadership.Value,
            snapshot.Positions.Values
                .OrderBy(entry => entry.Value.Id.Value, StringComparer.Ordinal)
                .Select(entry => new CommandRelationResponse(
                    entry.Value.Id.Value,
                    entry.Value.Unit.Value,
                    entry.Value.ReportsTo?.Value,
                    snapshot.Relations.Value
                        .GetDirectSubordinates(entry.Value.Id)
                        .Select(positionId => positionId.Value)
                        .OrderBy(positionId => positionId, StringComparer.Ordinal)
                        .ToArray()))
                .ToArray());
    }

    public static PositionConfigurationResponse MapPositionConfiguration(
        OrganizationRegistrySnapshot snapshot,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(positionId);

        var position = snapshot.Positions[positionId];
        var occupant = snapshot.Occupants[positionId];
        var authority = snapshot.Authorities[positionId];
        return new PositionConfigurationResponse(
            MapPosition(position),
            MapOccupant(occupant),
            new AuthorityResponse(
                authority.Value.CanDecide.ToArray(),
                authority.Value.MustEscalate.ToArray(),
                authority.Value.RequiresHumanApproval.ToArray(),
                authority.UpdatedAt),
            snapshot.Schedules
                .Where(pair => pair.Key.PositionId == positionId)
                .OrderBy(pair => pair.Key.ScheduleId, StringComparer.Ordinal)
                .Select(pair => new ScheduleResponse(
                    pair.Value.Value.ScheduleId,
                    pair.Value.Value.Cron,
                    pair.Value.Value.Instruction,
                    pair.Value.UpdatedAt))
                .ToArray());
    }

    internal static PositionResponse MapPosition(RegistryEntry<RegistryPosition> entry) =>
        new(
            entry.Value.Id.Value,
            entry.Value.Name,
            entry.Value.Unit.Value,
            entry.Value.ReportsTo?.Value,
            entry.Value.Timezone,
            entry.UpdatedAt);

    internal static OwnerResponse MapOwner(OwnerConfiguration owner) =>
        new(
            owner.Type switch
            {
                OwnerType.Human => "human",
                OwnerType.Group => "group",
                _ => throw new InvalidOperationException("Unknown organization owner type."),
            },
            owner.Ref);

    private static OccupantResponse MapOccupant(RegistryEntry<RegistryOccupant> entry) =>
        new(
            entry.Value.Type switch
            {
                OccupantType.AiAgent => "ai-agent",
                OccupantType.Human => "human",
                _ => throw new InvalidOperationException("Unknown position occupant type."),
            },
            entry.Value.IdentityPromptRef,
            entry.Value.Ai is null ? null : MapAi(entry.Value.Ai),
            entry.Value.WorkingHours is null
                ? null
                : new WorkingHoursResponse(
                    entry.Value.WorkingHours.Start,
                    entry.Value.WorkingHours.End),
            entry.Value.Subscriptions
                .Select(subscription => new SubscriptionResponse(
                    subscription.Event,
                    subscription.Within))
                .ToArray(),
            entry.Value.Tools
                .Select(tool => new ToolResponse(tool.Connector, tool.Scope.ToArray()))
                .ToArray(),
            entry.UpdatedAt);

    private static AiResponse MapAi(AiConfiguration ai) =>
        new(
            ai.Provider,
            ai.Model,
            ai.Temperature,
            ai.MaxTokens,
            ai.Processing,
            ai.BatchWindow,
            ai.Fallback
                .Select(fallback => new AiFallbackResponse(fallback.Provider, fallback.Model))
                .ToArray(),
            ai.Budget is null
                ? null
                : new BudgetResponse(
                    ai.Budget.ReactiveMaxEurPerDay,
                    ai.Budget.ProactiveMaxEurPerDay,
                    ai.Budget.TotalMaxEurPerDay,
                    ai.Budget.MaxCallsPerHour));
}
