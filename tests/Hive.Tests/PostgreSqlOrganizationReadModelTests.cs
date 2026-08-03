using Hive.Api.Organization;
using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.ReadModels;
using Hive.Infrastructure.Organization.ReadModels.PostgreSql;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Tests.PostgreSql;
using Npgsql;

namespace Hive.Tests;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOrganizationReadModelTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset ImportedAt =
        new(2026, 8, 2, 9, 0, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 2, 10, 30, 0, TimeSpan.Zero);

    [Fact]
    public async Task Registry_import_publishes_the_complete_ordered_organogram()
    {
        var imported = await ImportAsync(Configuration(), ImportedAt);
        await using var reader = SnapshotReader();
        var readModel = ReadModel(reader);

        var result = await readModel.ReadOrganogramAsync(
            OrganizationId.From("acme-delivery"),
            rootUnitId: null,
            CancellationToken.None);

        Assert.True(result.IsAvailable);
        var response = Assert.IsType<OrganogramResponse>(result.Value);
        Assert.Equal(imported.Snapshot!.Version, response.Registry.Version);
        Assert.Equal(imported.Snapshot.Fingerprint, response.Registry.Fingerprint);
        Assert.Equal(GeneratedAt, response.GeneratedAtUtc);
        Assert.Equal("raiz", response.RootUnitId);
        Assert.Equal("raiz", response.Organization.RootUnitId);
        Assert.Equal("ceo", response.Organization.RootPositionId);
        Assert.Equal(["engenharia", "raiz"], response.Units.Select(unit => unit.Id));
        Assert.Equal(
            "delivery-lead",
            response.Units.Single(unit => unit.Id == "engenharia").LeadershipPositionId);
        Assert.Equal(
            ["bug-triage", "ceo", "delivery-lead"],
            response.Positions.Select(position => position.Id));

        var ceo = response.Positions.Single(position => position.Id == "ceo");
        var deliveryLead = response.Positions.Single(position => position.Id == "delivery-lead");
        var bugTriage = response.Positions.Single(position => position.Id == "bug-triage");
        Assert.Null(ceo.Hierarchy.ReportsToPositionId);
        Assert.Equal(["delivery-lead"], ceo.Hierarchy.DirectSubordinatePositionIds);
        Assert.Equal("ceo", deliveryLead.Hierarchy.ReportsToPositionId);
        Assert.Equal(["bug-triage"], deliveryLead.Hierarchy.DirectSubordinatePositionIds);
        Assert.Equal("delivery-lead", bugTriage.Hierarchy.ReportsToPositionId);
        Assert.Empty(bugTriage.Hierarchy.DirectSubordinatePositionIds);
        Assert.Equal(
            "configured-ai:acme-delivery/delivery-lead",
            deliveryLead.Occupant.Id);
        Assert.Equal(OrganizationOccupantType.AiAgent, deliveryLead.Occupant.Type);
        Assert.All(response.Positions, position =>
        {
            Assert.Equal(PositionOperationalState.Idle, position.OperationalState.State);
            Assert.Equal(0, position.OperationalState.Sequence);
            Assert.Equal(ImportedAt, position.OperationalState.UpdatedAtUtc);
        });
    }

    [Fact]
    public async Task Unit_query_returns_only_the_requested_unit_subtree_without_rewriting_relations()
    {
        await ImportAsync(Configuration(), ImportedAt);
        await using var reader = SnapshotReader();
        var readModel = ReadModel(reader);

        var result = await readModel.ReadOrganogramAsync(
            OrganizationId.From("acme-delivery"),
            UnitId.From("engenharia"),
            CancellationToken.None);

        var response = Assert.IsType<OrganogramResponse>(result.Value);
        Assert.Equal("engenharia", response.RootUnitId);
        var unit = Assert.Single(response.Units);
        Assert.Equal("engenharia", unit.Id);
        Assert.Equal("raiz", unit.ParentUnitId);
        Assert.Equal(
            ["bug-triage", "delivery-lead"],
            response.Positions.Select(position => position.Id));
        var deliveryLead = response.Positions.Single(position => position.Id == "delivery-lead");
        Assert.Equal("ceo", deliveryLead.Hierarchy.ReportsToPositionId);
        Assert.Equal(["bug-triage"], deliveryLead.Hierarchy.DirectSubordinatePositionIds);
    }

    [Fact]
    public async Task Position_and_initial_state_queries_share_the_published_registry_version()
    {
        var imported = await ImportAsync(Configuration(), ImportedAt);
        await using var reader = SnapshotReader();
        var readModel = ReadModel(reader);

        var positionResult = await readModel.ReadPositionAsync(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"),
            CancellationToken.None);
        var statesResult = await readModel.ReadPositionStatesAsync(
            OrganizationId.From("acme-delivery"),
            CancellationToken.None);

        var position = Assert.IsType<PositionDetailResponse>(positionResult.Value);
        var states = Assert.IsType<PositionStatesResponse>(statesResult.Value);
        Assert.Equal(imported.Snapshot!.Version, position.Registry.Version);
        Assert.Equal(position.Registry, states.Registry);
        Assert.Equal("delivery-lead", position.Position.Id);
        Assert.Null(states.LastEventAppliedAtUtc);
        Assert.Equal(
            ["bug-triage", "ceo", "delivery-lead"],
            states.States.Select(state => state.PositionId));
        Assert.All(states.States, state => Assert.Equal(PositionOperationalState.Idle, state.State));
    }

    [Fact]
    public async Task Live_state_advances_monotonically_and_is_exposed_by_every_position_view()
    {
        var configuration = Configuration();
        await ImportAsync(configuration, ImportedAt);
        var workingAt = ImportedAt.AddMinutes(5);
        var blockedAt = ImportedAt.AddMinutes(10);
        var taskThreadId = Guid.Parse("b0871395-704d-4d25-90b7-ae8278fe7b7a");
        var escalationThreadId = Guid.Parse("c6102135-ced8-48db-a51b-e9c337acac3b");
        await using (var writer = StateWriter())
        {
            var working = await writer.AdvanceAsync(
                configuration.Organization.Id,
                PositionId.From("delivery-lead"),
                PositionLiveState.Working,
                workingAt,
                new PositionLiveStateCorrelatedEvent("TaskCreated", taskThreadId, workingAt));
            var blocked = await writer.AdvanceAsync(
                configuration.Organization.Id,
                PositionId.From("delivery-lead"),
                PositionLiveState.Blocked,
                blockedAt,
                new PositionLiveStateCorrelatedEvent(
                    "Escalation",
                    escalationThreadId,
                    blockedAt));

            Assert.Equal(1, working.Sequence);
            Assert.Equal(2, blocked.Sequence);
        }

        await using var reader = SnapshotReader();
        var readModel = ReadModel(reader);
        var organogramResult = await readModel.ReadOrganogramAsync(
            configuration.Organization.Id,
            rootUnitId: null,
            CancellationToken.None);
        var positionResult = await readModel.ReadPositionAsync(
            configuration.Organization.Id,
            PositionId.From("delivery-lead"),
            CancellationToken.None);
        var statesResult = await readModel.ReadPositionStatesAsync(
            configuration.Organization.Id,
            CancellationToken.None);

        var organogram = Assert.IsType<OrganogramResponse>(organogramResult.Value);
        var detail = Assert.IsType<PositionDetailResponse>(positionResult.Value);
        var states = Assert.IsType<PositionStatesResponse>(statesResult.Value);
        var embedded = organogram.Positions.Single(position => position.Id == "delivery-lead")
            .OperationalState;
        var snapshot = states.States.Single(state => state.PositionId == "delivery-lead");
        Assert.Equal(embedded, detail.Position.OperationalState);
        Assert.Equal(embedded, snapshot);
        Assert.Equal(PositionOperationalState.Blocked, snapshot.State);
        Assert.Equal(2, snapshot.Sequence);
        Assert.Equal(blockedAt, snapshot.UpdatedAtUtc);
        Assert.Equal("Escalation", snapshot.LastCorrelatedEvent!.Type);
        Assert.Equal(escalationThreadId, snapshot.LastCorrelatedEvent.ThreadId);
        Assert.Equal(blockedAt, snapshot.LastCorrelatedEvent.OccurredAtUtc);
    }

    [Fact]
    public async Task Structural_reimport_preserves_the_existing_live_state()
    {
        var configuration = Configuration();
        await ImportAsync(configuration, ImportedAt);
        var blockedAt = ImportedAt.AddMinutes(10);
        var threadId = Guid.Parse("83c2158d-b0db-40a4-95a4-99f276e52bdd");
        await using (var writer = StateWriter())
        {
            await writer.AdvanceAsync(
                configuration.Organization.Id,
                PositionId.From("delivery-lead"),
                PositionLiveState.Blocked,
                blockedAt,
                new PositionLiveStateCorrelatedEvent("Escalation", threadId, blockedAt));
        }

        await ImportWithoutResetAsync(
            WithRenamedDeliveryLead(configuration),
            ImportedAt.AddHours(1));
        await using var reader = SnapshotReader();
        var result = await ReadModel(reader).ReadPositionAsync(
            configuration.Organization.Id,
            PositionId.From("delivery-lead"),
            CancellationToken.None);

        var response = Assert.IsType<PositionDetailResponse>(result.Value);
        Assert.Equal("Engineering Lead", response.Position.Name);
        Assert.Equal(PositionOperationalState.Blocked, response.Position.OperationalState.State);
        Assert.Equal(1, response.Position.OperationalState.Sequence);
        Assert.Equal(blockedAt, response.Position.OperationalState.UpdatedAtUtc);
        Assert.Equal(threadId, response.Position.OperationalState.LastCorrelatedEvent!.ThreadId);
    }

    [Fact]
    public async Task State_advance_rejects_a_position_outside_the_current_read_model()
    {
        var configuration = Configuration();
        await ImportAsync(configuration, ImportedAt);
        await using var writer = StateWriter();

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await writer.AdvanceAsync(
                configuration.Organization.Id,
                PositionId.From("missing-position"),
                PositionLiveState.Working,
                ImportedAt.AddMinutes(1)));

        Assert.Contains("does not have a live-state row", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Uncorrelated_state_change_preserves_the_last_correlated_event()
    {
        var configuration = Configuration();
        await ImportAsync(configuration, ImportedAt);
        var blockedAt = ImportedAt.AddMinutes(10);
        var offlineAt = ImportedAt.AddHours(10);
        var threadId = Guid.Parse("59a876f0-ff3c-43e0-b30e-d29c5d9793e9");
        await using var writer = StateWriter();
        await writer.AdvanceAsync(
            configuration.Organization.Id,
            PositionId.From("delivery-lead"),
            PositionLiveState.Blocked,
            blockedAt,
            new PositionLiveStateCorrelatedEvent("Escalation", threadId, blockedAt));

        var offline = await writer.AdvanceAsync(
            configuration.Organization.Id,
            PositionId.From("delivery-lead"),
            PositionLiveState.Offline,
            offlineAt);

        Assert.Equal(PositionLiveState.Offline, offline.State);
        Assert.Equal(2, offline.Sequence);
        Assert.Equal(offlineAt, offline.UpdatedAtUtc);
        Assert.Equal(threadId, offline.LastCorrelatedEvent!.ThreadId);
        Assert.Equal(blockedAt, offline.LastCorrelatedEvent.OccurredAtUtc);
    }

    [Fact]
    public async Task Missing_resources_are_distinct_from_an_unconfigured_read_model()
    {
        await ResetAndMigrateAsync();
        await using var configuredReader = SnapshotReader();
        var configured = ReadModel(configuredReader);
        await using var unavailableReader = new PostgreSqlOrganogramSnapshotReader(
            connectionString: null);
        var unavailable = ReadModel(unavailableReader);

        var missingOrganization = await configured.ReadOrganogramAsync(
            OrganizationId.From("missing"),
            rootUnitId: null,
            CancellationToken.None);
        var unavailableOrganization = await unavailable.ReadOrganogramAsync(
            OrganizationId.From("missing"),
            rootUnitId: null,
            CancellationToken.None);

        Assert.True(missingOrganization.IsAvailable);
        Assert.Null(missingOrganization.Value);
        Assert.False(unavailableOrganization.IsAvailable);
        Assert.Null(unavailableOrganization.Value);
    }

    [Fact]
    public async Task Changed_import_atomically_replaces_the_current_organogram_version()
    {
        var configuration = Configuration();
        await ImportAsync(configuration, ImportedAt);
        var changedAt = ImportedAt.AddHours(1);
        var changed = await ImportWithoutResetAsync(
            WithRenamedDeliveryLead(configuration),
            changedAt);
        await using var reader = SnapshotReader();
        var readModel = ReadModel(reader);

        var result = await readModel.ReadPositionAsync(
            configuration.Organization.Id,
            PositionId.From("delivery-lead"),
            CancellationToken.None);

        var response = Assert.IsType<PositionDetailResponse>(result.Value);
        Assert.Equal(2, changed.Snapshot!.Version);
        Assert.Equal(changed.Snapshot.Fingerprint, response.Registry.Fingerprint);
        Assert.Equal("Engineering Lead", response.Position.Name);
        Assert.Equal(ImportedAt, response.Position.OperationalState.UpdatedAtUtc);

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT count(*), min(registry_version), max(registry_version)
            FROM organogram.snapshots
            WHERE organization_id = 'acme-delivery';
            """);
        await using var databaseReader = await command.ExecuteReaderAsync();
        Assert.True(await databaseReader.ReadAsync());
        Assert.Equal(1, databaseReader.GetInt64(0));
        Assert.Equal(2, databaseReader.GetInt64(1));
        Assert.Equal(2, databaseReader.GetInt64(2));
    }

    [Fact]
    public async Task Organogram_publication_failure_rolls_back_the_registry_import()
    {
        var configuration = Configuration();
        await ImportAsync(configuration, ImportedAt);
        await using (var dataSource = fixture.CreateDataSource())
        await using (var command = dataSource.CreateCommand(
            """
            CREATE FUNCTION organogram.reject_second_version()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'forced organogram publication failure';
            END;
            $$;

            CREATE TRIGGER reject_second_version
            BEFORE INSERT ON organogram.positions
            FOR EACH ROW
            WHEN (NEW.registry_version = 2)
            EXECUTE FUNCTION organogram.reject_second_version();
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        var exception = await Assert.ThrowsAsync<PostgresException>(() =>
            ImportWithoutResetAsync(
                WithRenamedDeliveryLead(configuration),
                ImportedAt.AddHours(1)));
        await using var reader = SnapshotReader();
        var readModel = ReadModel(reader);
        var result = await readModel.ReadPositionAsync(
            configuration.Organization.Id,
            PositionId.From("delivery-lead"),
            CancellationToken.None);

        Assert.Contains(
            "forced organogram publication failure",
            exception.MessageText,
            StringComparison.Ordinal);
        var response = Assert.IsType<PositionDetailResponse>(result.Value);
        Assert.Equal(1, response.Registry.Version);
        Assert.Equal("Delivery Lead", response.Position.Name);

        await using var registryDataSource = fixture.CreateDataSource();
        var registry = new PostgreSqlOrganizationRegistry(registryDataSource);
        var snapshot = await registry.FindSnapshotAsync(configuration.Organization.Id);
        Assert.Equal(1, snapshot!.Version);
        Assert.Equal("Delivery Lead", snapshot.Positions[PositionId.From("delivery-lead")].Value.Name);
    }

    private async Task<OrganizationImportResult> ImportAsync(
        OrganizationConfiguration configuration,
        DateTimeOffset importedAt)
    {
        await ResetAndMigrateAsync();
        return await ImportWithoutResetAsync(configuration, importedAt);
    }

    private async Task<OrganizationImportResult> ImportWithoutResetAsync(
        OrganizationConfiguration configuration,
        DateTimeOffset importedAt)
    {
        await using var dataSource = fixture.CreateDataSource();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        return await new OrganizationConfigurationImporter(
                registry,
                new ManualTimeProvider(importedAt))
            .ImportAsync(configuration);
    }

    private async Task ResetAndMigrateAsync()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
    }

    private PostgreSqlOrganogramSnapshotReader SnapshotReader() =>
        new(fixture.ConnectionString);

    private PostgreSqlPositionLiveStateWriter StateWriter() =>
        new(fixture.ConnectionString);

    private static OrganizationReadModel ReadModel(
        PostgreSqlOrganogramSnapshotReader reader) =>
        new(reader, new ManualTimeProvider(GeneratedAt));

    private static OrganizationConfiguration Configuration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(
                RepositoryRoot,
                "config",
                "organizations",
                "acme-delivery",
                "organization.yaml"));
        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static OrganizationConfiguration WithRenamedDeliveryLead(
        OrganizationConfiguration configuration) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        position.Occupant,
                        position.ReportsTo,
                        "Engineering Lead",
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
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
