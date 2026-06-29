using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class OrganizationRegistryProjection
{
    private OrganizationRegistryProjection(
        OrganizationId organizationId,
        ProjectedEntry<RegistryOrganization> organization,
        IReadOnlyDictionary<UnitId, ProjectedEntry<RegistryUnit>> units,
        IReadOnlyDictionary<PositionId, ProjectedEntry<RegistryPosition>> positions,
        IReadOnlyDictionary<PositionId, ProjectedEntry<RegistryOccupant>> occupants,
        IReadOnlyDictionary<PositionId, ProjectedEntry<RegistryAuthority>> authorities,
        IReadOnlyDictionary<RegistryScheduleKey, ProjectedEntry<RegistrySchedule>> schedules,
        ProjectedEntry<OrganizationRelationsSnapshot> relations,
        string fingerprint)
    {
        OrganizationId = organizationId;
        Organization = organization;
        Units = units;
        Positions = positions;
        Occupants = occupants;
        Authorities = authorities;
        Schedules = schedules;
        Relations = relations;
        Fingerprint = fingerprint;
    }

    public OrganizationId OrganizationId { get; }

    public ProjectedEntry<RegistryOrganization> Organization { get; }

    public IReadOnlyDictionary<UnitId, ProjectedEntry<RegistryUnit>> Units { get; }

    public IReadOnlyDictionary<PositionId, ProjectedEntry<RegistryPosition>> Positions { get; }

    public IReadOnlyDictionary<PositionId, ProjectedEntry<RegistryOccupant>> Occupants { get; }

    public IReadOnlyDictionary<PositionId, ProjectedEntry<RegistryAuthority>> Authorities { get; }

    public IReadOnlyDictionary<RegistryScheduleKey, ProjectedEntry<RegistrySchedule>> Schedules { get; }

    public ProjectedEntry<OrganizationRelationsSnapshot> Relations { get; }

    public string Fingerprint { get; }

    public static OrganizationRegistryProjection Create(OrganizationConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var organization = Project(new RegistryOrganization(
            configuration.Organization.Id,
            configuration.Organization.Name,
            configuration.Organization.RootUnit,
            new OwnerConfiguration(
                configuration.Organization.Owner.Type,
                configuration.Organization.Owner.Ref),
            ReadOnly(configuration.Prompts
                .OrderBy(prompt => prompt.Id, StringComparer.Ordinal)
                .Select(prompt => new PromptConfiguration(prompt.Id, prompt.Path)))));

        var units = configuration.Units
            .OrderBy(unit => unit.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                unit => unit.Id,
                unit => Project(new RegistryUnit(unit.Id, unit.Name, unit.Parent, unit.Leadership)));

        var positions = configuration.Positions
            .OrderBy(position => position.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                position => position.Id,
                position => Project(new RegistryPosition(
                    position.Id,
                    position.Name,
                    position.Unit,
                    position.ReportsTo,
                    position.Timezone)));

        var occupants = configuration.Positions
            .OrderBy(position => position.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                position => position.Id,
                position => Project(ProjectOccupant(position)));

        var authorities = configuration.Positions
            .OrderBy(position => position.Id.Value, StringComparer.Ordinal)
            .ToDictionary(
                position => position.Id,
                position => Project(ProjectAuthority(position)));

        var schedules = configuration.Positions
            .SelectMany(position => position.Occupant.Schedule.Select(schedule => new RegistrySchedule(
                position.Id,
                schedule.Id,
                schedule.Cron,
                schedule.Instruction)))
            .OrderBy(schedule => schedule.PositionId.Value, StringComparer.Ordinal)
            .ThenBy(schedule => schedule.ScheduleId, StringComparer.Ordinal)
            .ToDictionary(
                schedule => new RegistryScheduleKey(schedule.PositionId, schedule.ScheduleId),
                Project);

        var relationsSnapshot = BuildRelations(configuration);
        var relationDescriptor = configuration.Positions
            .OrderBy(position => position.Id.Value, StringComparer.Ordinal)
            .Select(position => new
            {
                Position = position.Id.Value,
                Unit = position.Unit.Value,
                ReportsTo = position.ReportsTo?.Value,
            })
            .ToArray();
        var relations = new ProjectedEntry<OrganizationRelationsSnapshot>(
            relationsSnapshot,
            ComputeFingerprint(relationDescriptor));

        var entityFingerprints = new List<(RegistryEntityKind Kind, string Key, string Fingerprint)>
        {
            (RegistryEntityKind.Organization, configuration.Organization.Id.Value, organization.Fingerprint),
            (RegistryEntityKind.CommandRelations, configuration.Organization.Id.Value, relations.Fingerprint),
        };
        entityFingerprints.AddRange(units.Select(pair =>
            (RegistryEntityKind.Unit, pair.Key.Value, pair.Value.Fingerprint)));
        entityFingerprints.AddRange(positions.Select(pair =>
            (RegistryEntityKind.Position, pair.Key.Value, pair.Value.Fingerprint)));
        entityFingerprints.AddRange(occupants.Select(pair =>
            (RegistryEntityKind.Occupant, pair.Key.Value, pair.Value.Fingerprint)));
        entityFingerprints.AddRange(authorities.Select(pair =>
            (RegistryEntityKind.Authority, pair.Key.Value, pair.Value.Fingerprint)));
        entityFingerprints.AddRange(schedules.Select(pair =>
            (RegistryEntityKind.Schedule, pair.Key.ToString(), pair.Value.Fingerprint)));

        var canonical = string.Join(
            '\n',
            entityFingerprints
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Key, StringComparer.Ordinal)
                .Select(item => $"{(int)item.Kind}\0{item.Key}\0{item.Fingerprint}"));

        return new OrganizationRegistryProjection(
            configuration.Organization.Id,
            organization,
            new ReadOnlyDictionary<UnitId, ProjectedEntry<RegistryUnit>>(units),
            new ReadOnlyDictionary<PositionId, ProjectedEntry<RegistryPosition>>(positions),
            new ReadOnlyDictionary<PositionId, ProjectedEntry<RegistryOccupant>>(occupants),
            new ReadOnlyDictionary<PositionId, ProjectedEntry<RegistryAuthority>>(authorities),
            new ReadOnlyDictionary<RegistryScheduleKey, ProjectedEntry<RegistrySchedule>>(schedules),
            relations,
            ComputeFingerprint(canonical));
    }

    private static RegistryOccupant ProjectOccupant(PositionConfiguration position)
    {
        var occupant = position.Occupant;
        return new RegistryOccupant(
            position.Id,
            occupant.Type,
            occupant.IdentityPromptRef,
            Clone(occupant.Ai),
            occupant.WorkingHours is null
                ? null
                : new WorkingHoursConfiguration(
                    occupant.WorkingHours.Start,
                    occupant.WorkingHours.End),
            ReadOnly(occupant.Subscriptions
                .OrderBy(subscription => subscription.Event, StringComparer.Ordinal)
                .Select(subscription => new SubscriptionConfiguration(
                    subscription.Event,
                    subscription.Within))),
            ReadOnly(occupant.Tools
                .Select(tool => new ToolConfiguration(
                    tool.Connector,
                    ReadOnly(tool.Scope.OrderBy(value => value, StringComparer.Ordinal))))
                .OrderBy(tool => tool.Connector, StringComparer.Ordinal)
                .ThenBy(
                    tool => string.Join('\0', tool.Scope),
                    StringComparer.Ordinal)));
    }

    private static RegistryAuthority ProjectAuthority(PositionConfiguration position)
    {
        var authority = position.Occupant.Authority;
        return new RegistryAuthority(
            position.Id,
            Sorted(authority?.CanDecide),
            Sorted(authority?.MustEscalate),
            Sorted(authority?.RequiresHumanApproval));
    }

    private static AiConfiguration? Clone(AiConfiguration? ai)
    {
        if (ai is null)
        {
            return null;
        }

        var budget = ai.Budget is null
            ? null
            : new BudgetConfiguration(
                ai.Budget.ReactiveMaxEurPerDay,
                ai.Budget.ProactiveMaxEurPerDay,
                ai.Budget.TotalMaxEurPerDay,
                ai.Budget.MaxCallsPerHour);

        return new AiConfiguration(
            ai.Provider,
            ai.Model,
            ai.Temperature,
            ai.MaxTokens,
            ai.Processing,
            ai.BatchWindow,
            ReadOnly(ai.Fallback.Select(item => new AiFallbackConfiguration(item.Provider, item.Model))),
            budget,
            ai.Timeout);
    }

    private static OrganizationRelationsSnapshot BuildRelations(OrganizationConfiguration configuration)
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(
            configuration.Organization.Id,
            new OrganizationOwnerEndpointRef());

        foreach (var position in configuration.Positions)
        {
            builder.AddPosition(position.Id, position.Unit, position.ReportsTo);
        }

        var snapshot = builder.Build();
        var rootUnitLeadership = configuration.Units
            .Single(unit => unit.Id == configuration.Organization.RootUnit)
            .Leadership;

        if (snapshot.RootUnitLeadership != rootUnitLeadership)
        {
            throw new InvalidOperationException(
                $"Configured root-unit leadership '{rootUnitLeadership.Value}' does not match "
                + $"the command-tree root '{snapshot.RootUnitLeadership.Value}'.");
        }

        return snapshot;
    }

    private static ProjectedEntry<T> Project<T>(T value) => new(value, ComputeFingerprint(value));

    private static IReadOnlyList<string> Sorted(IReadOnlyList<string>? values) =>
        ReadOnly((values ?? Array.Empty<string>()).OrderBy(value => value, StringComparer.Ordinal));

    private static IReadOnlyList<T> ReadOnly<T>(IEnumerable<T> values) =>
        Array.AsReadOnly(values.ToArray());

    private static string ComputeFingerprint<T>(T value)
    {
        var bytes = value is string text
            ? Encoding.UTF8.GetBytes(text)
            : JsonSerializer.SerializeToUtf8Bytes(value);
        return $"sha256:{Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant()}";
    }

    internal sealed record ProjectedEntry<T>(T Value, string Fingerprint);
}
