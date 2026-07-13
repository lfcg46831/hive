using Hive.Domain.Organization.Configuration;
using Hive.Domain.Identity;
using Hive.Domain.Governance;
using Hive.Domain.Organization;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class OrganizationConfigurationImporterTests
{
    private static readonly DateTimeOffset FirstImportAt =
        new(2026, 6, 23, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Import_materializes_the_validated_example_with_a_deterministic_plan()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(FirstImportAt));

        var configuration = ExampleConfiguration();
        var result = await importer.ImportAsync(configuration);

        Assert.Equal(OrganizationImportStatus.Applied, result.Status);
        Assert.Empty(result.ValidationErrors);
        Assert.NotNull(result.Plan);
        Assert.NotNull(result.Snapshot);

        var snapshot = result.Snapshot!;
        Assert.Equal(1, snapshot.Version);
        Assert.StartsWith("sha256:", snapshot.Fingerprint, StringComparison.Ordinal);
        Assert.Equal(FirstImportAt, snapshot.ImportedAt);
        Assert.Equal(2, snapshot.Units.Count);
        Assert.Equal(3, snapshot.Positions.Count);
        Assert.Equal(3, snapshot.Occupants.Count);
        Assert.Equal(3, snapshot.Authorities.Count);
        Assert.Single(snapshot.Schedules);
        var schedule = Assert.Single(snapshot.Schedules).Value.Value;
        Assert.True(schedule.IsActive);
        Assert.Equal("normal", schedule.Priority);
        Assert.False(schedule.IsCritical);
        Assert.Equal("skip", schedule.CatchUp);
        Assert.Equal("ceo", snapshot.Relations.Value.RootUnitLeadership.Value);

        Assert.Equal(
            [
                (RegistryEntityKind.Organization, "acme-delivery"),
                (RegistryEntityKind.Unit, "engenharia"),
                (RegistryEntityKind.Unit, "raiz"),
                (RegistryEntityKind.Position, "bug-triage"),
                (RegistryEntityKind.Position, "ceo"),
                (RegistryEntityKind.Position, "delivery-lead"),
                (RegistryEntityKind.Occupant, "bug-triage"),
                (RegistryEntityKind.Occupant, "ceo"),
                (RegistryEntityKind.Occupant, "delivery-lead"),
                (RegistryEntityKind.Authority, "bug-triage"),
                (RegistryEntityKind.Authority, "ceo"),
                (RegistryEntityKind.Authority, "delivery-lead"),
                (RegistryEntityKind.Schedule, "delivery-lead/relatorio-diario"),
                (RegistryEntityKind.CommandRelations, "acme-delivery"),
                (RegistryEntityKind.ActionDomainCatalog, "acme-delivery"),
            ],
            result.Plan!.Changes.Select(change => (change.EntityKind, change.Key)));
        Assert.All(
            result.Plan.Changes,
            change => Assert.Equal(RegistryChangeKind.Added, change.Kind));

        IOrganizationRegistryReader reader = registry;
        var published = await reader.FindSnapshotAsync(configuration.Organization.Id);
        Assert.Same(result.Snapshot, published);
    }

    [Fact]
    public async Task Reordered_semantically_equivalent_configuration_is_a_timestamp_preserving_no_op()
    {
        var registry = new InMemoryOrganizationRegistry();
        var clock = new ManualTimeProvider(FirstImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);
        var configuration = ExampleConfiguration();

        var first = await importer.ImportAsync(configuration);
        clock.UtcNow = FirstImportAt.AddHours(1);

        var second = await importer.ImportAsync(Reordered(configuration));

        Assert.Equal(OrganizationImportStatus.NoChanges, second.Status);
        Assert.Same(first.Snapshot, second.Snapshot);
        Assert.Equal(first.Snapshot!.Fingerprint, second.Snapshot!.Fingerprint);
        Assert.Equal(1, second.Snapshot.Version);
        Assert.Equal(FirstImportAt, second.Snapshot.ImportedAt);
        Assert.NotNull(second.Plan);
        Assert.Equal(1, second.Plan!.TargetVersion);
        Assert.Empty(second.Plan.Changes);
        Assert.All(second.Snapshot.Units.Values, entry => Assert.Equal(FirstImportAt, entry.UpdatedAt));
        Assert.All(second.Snapshot.Positions.Values, entry => Assert.Equal(FirstImportAt, entry.UpdatedAt));
    }

    [Fact]
    public async Task Reordered_set_like_occupant_entries_have_the_same_fingerprint()
    {
        var registry = new InMemoryOrganizationRegistry();
        var clock = new ManualTimeProvider(FirstImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);
        var configuration = WithSetLikeOccupantEntries(ExampleConfiguration());
        var first = await importer.ImportAsync(configuration);
        clock.UtcNow = FirstImportAt.AddHours(1);

        var second = await importer.ImportAsync(Reordered(configuration));

        Assert.Equal(OrganizationImportStatus.NoChanges, second.Status);
        Assert.Equal(first.Snapshot!.Fingerprint, second.Snapshot!.Fingerprint);
        Assert.Same(first.Snapshot, second.Snapshot);
    }

    [Fact]
    public async Task Changed_configuration_updates_only_changed_entries_and_removes_stale_entries()
    {
        var registry = new InMemoryOrganizationRegistry();
        var clock = new ManualTimeProvider(FirstImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);
        var configuration = ExampleConfiguration();
        var first = await importer.ImportAsync(configuration);
        var secondImportAt = FirstImportAt.AddHours(1);
        clock.UtcNow = secondImportAt;

        var second = await importer.ImportAsync(WithRenamedDeliveryLeadAndNoSchedule(configuration));

        Assert.Equal(OrganizationImportStatus.Applied, second.Status);
        Assert.Equal(2, second.Snapshot!.Version);
        Assert.Equal(secondImportAt, second.Snapshot.ImportedAt);
        Assert.Empty(second.Snapshot.Schedules);
        Assert.Equal(
            [
                (RegistryEntityKind.Position, "delivery-lead", RegistryChangeKind.Updated),
                (RegistryEntityKind.Schedule, "delivery-lead/relatorio-diario", RegistryChangeKind.Removed),
            ],
            second.Plan!.Changes.Select(change => (change.EntityKind, change.Key, change.Kind)));

        var ceo = PositionId.From("ceo");
        var deliveryLead = PositionId.From("delivery-lead");
        Assert.Same(first.Snapshot!.Positions[ceo], second.Snapshot.Positions[ceo]);
        Assert.Equal(FirstImportAt, second.Snapshot.Positions[ceo].UpdatedAt);
        Assert.Equal(secondImportAt, second.Snapshot.Positions[deliveryLead].UpdatedAt);
        Assert.Same(first.Snapshot.Relations, second.Snapshot.Relations);
    }

    [Fact]
    public async Task Invalid_configuration_does_not_replace_the_published_snapshot()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(FirstImportAt));
        var configuration = ExampleConfiguration();
        var first = await importer.ImportAsync(configuration);
        var duplicateUnit = new OrganizationConfiguration(
            configuration.Organization,
            [.. configuration.Units, configuration.Units[0]],
            configuration.Positions,
            configuration.Prompts);

        var invalid = await importer.ImportAsync(duplicateUnit);

        Assert.Equal(OrganizationImportStatus.Invalid, invalid.Status);
        Assert.Null(invalid.Plan);
        Assert.Null(invalid.Snapshot);
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Code == "duplicate-unit-id");
        Assert.True(registry.TryGetSnapshot(configuration.Organization.Id, out var published));
        Assert.Same(first.Snapshot, published);
    }

    [Fact]
    public async Task Invalid_command_relations_do_not_replace_the_published_snapshot()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(FirstImportAt));
        var configuration = ExampleConfiguration();
        var first = await importer.ImportAsync(configuration);
        var positions = configuration.Positions
            .Select(position => new PositionConfiguration(
                position.Id,
                position.Unit,
                position.Occupant,
                position.Id.Value == "ceo"
                    ? PositionId.From("delivery-lead")
                    : PositionId.From("ceo"),
                position.Name,
                position.Timezone))
            .ToArray();

        var invalid = await importer.ImportAsync(new OrganizationConfiguration(
            configuration.Organization,
            configuration.Units,
            positions,
            configuration.Prompts));

        Assert.Equal(OrganizationImportStatus.Invalid, invalid.Status);
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Code == "command-relations-invalid");
        Assert.True(registry.TryGetSnapshot(configuration.Organization.Id, out var published));
        Assert.Same(first.Snapshot, published);
    }

    [Fact]
    public async Task Self_referential_command_relation_is_reported_as_invalid_input()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var configuration = ExampleConfiguration();
        var positions = configuration.Positions
            .Select(position => position.Id.Value == "delivery-lead"
                ? new PositionConfiguration(
                    position.Id,
                    position.Unit,
                    position.Occupant,
                    position.Id,
                    position.Name,
                    position.Timezone)
                : position)
            .ToArray();

        var invalid = await importer.ImportAsync(new OrganizationConfiguration(
            configuration.Organization,
            configuration.Units,
            positions,
            configuration.Prompts));

        Assert.Equal(OrganizationImportStatus.Invalid, invalid.Status);
        Assert.Contains(
            invalid.ValidationErrors,
            error => error.Code == "command-relations-invalid");
        Assert.False(registry.TryGetSnapshot(configuration.Organization.Id, out _));
    }

    [Fact]
    public async Task Imported_registry_serves_live_organization_relations()
    {
        var registry = new InMemoryOrganizationRegistry();
        var configuration = ExampleConfiguration();
        await new OrganizationConfigurationImporter(registry).ImportAsync(configuration);
        IOrganizationRelations relations = registry;
        var organizationId = configuration.Organization.Id;
        var bugTriage = PositionId.From("bug-triage");
        var deliveryLead = PositionId.From("delivery-lead");

        Assert.Equal(
            PositionId.From("ceo"),
            await relations.GetDirectSuperiorAsync(organizationId, deliveryLead));
        Assert.Equal(
            deliveryLead,
            await relations.GetDirectSuperiorAsync(organizationId, bugTriage));
        Assert.Equal(
            UnitId.From("engenharia"),
            await relations.GetUnitOfPositionAsync(organizationId, deliveryLead));
        Assert.Equal(
            deliveryLead,
            Assert.Single(await relations.GetDirectSubordinatesAsync(
                organizationId,
                PositionId.From("ceo"))));
        Assert.Equal(
            bugTriage,
            Assert.Single(await relations.GetDirectSubordinatesAsync(organizationId, deliveryLead)));
        Assert.Equal(
            PositionId.From("ceo"),
            await relations.GetRootUnitLeadershipAsync(organizationId));
    }

    private static OrganizationConfiguration ExampleConfiguration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(RepositoryRoot, "config", "organizations", "acme-delivery", "organization.yaml"));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static OrganizationConfiguration Reordered(OrganizationConfiguration configuration) =>
        new(
            configuration.Organization,
            configuration.Units.Reverse().ToArray(),
            configuration.Positions
                .Reverse()
                .Select(position => new PositionConfiguration(
                    position.Id,
                    position.Unit,
                    Reordered(position.Occupant),
                    position.ReportsTo,
                    position.Name,
                    position.Timezone))
                .ToArray(),
            configuration.Prompts.Reverse().ToArray());

    private static OccupantConfiguration Reordered(OccupantConfiguration occupant) =>
        new(
            occupant.Type,
            occupant.IdentityPromptRef,
            occupant.Ai,
            occupant.WorkingHours,
            occupant.Authority is null
                ? null
                : new AuthorityConfiguration(
                    occupant.Authority.CanDecide.Select(key => key.Value).Reverse().ToArray(),
                    occupant.Authority.Overrides.Reverse().ToArray()),
            occupant.Schedule.Reverse().ToArray(),
            occupant.Subscriptions.Reverse().ToArray(),
            occupant.Tools.Reverse().ToArray());

    private static OrganizationConfiguration WithRenamedDeliveryLeadAndNoSchedule(
        OrganizationConfiguration configuration) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        new OccupantConfiguration(
                            position.Occupant.Type,
                            position.Occupant.IdentityPromptRef,
                            position.Occupant.Ai,
                            position.Occupant.WorkingHours,
                            position.Occupant.Authority,
                            [],
                            position.Occupant.Subscriptions,
                            position.Occupant.Tools),
                        position.ReportsTo,
                        "Engineering Lead",
                        position.Timezone)
                    : position)
                .ToArray(),
            configuration.Prompts);

    private static OrganizationConfiguration WithSetLikeOccupantEntries(
        OrganizationConfiguration configuration) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        new OccupantConfiguration(
                            position.Occupant.Type,
                            position.Occupant.IdentityPromptRef,
                            position.Occupant.Ai,
                            position.Occupant.WorkingHours,
                            new AuthorityConfiguration(
                                ["delivery.bug-triage", "support.incident-prioritization"],
                                [
                                    new AuthorityOverrideConfiguration(
                                        "comms.external-official",
                                        ActionDomainGate.HumanApproval,
                                        "ceo"),
                                    new AuthorityOverrideConfiguration(
                                        "delivery.release-prod",
                                        ActionDomainGate.HumanApproval,
                                        "ceo"),
                                ]),
                            position.Occupant.Schedule,
                            [
                                new SubscriptionConfiguration("work-item-created", "PT1H"),
                                new SubscriptionConfiguration("directive-deadline-approaching", "PT4H"),
                            ],
                            [
                                new ToolConfiguration("http", ["https://b.example/*"]),
                                new ToolConfiguration("http", ["https://a.example/*"]),
                            ]),
                        position.ReportsTo,
                        position.Name,
                        position.Timezone)
                    : position)
                .ToArray(),
            configuration.Prompts);

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
