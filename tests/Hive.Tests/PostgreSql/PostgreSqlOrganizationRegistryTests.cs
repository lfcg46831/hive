using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlOrganizationRegistryTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset FirstImportAt =
        new(2026, 6, 24, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task First_import_survives_a_new_connection()
    {
        await fixture.ResetRegistryAsync();
        OrganizationImportResult imported;

        await using (var firstDataSource = fixture.CreateDataSource())
        {
            await new PostgreSqlOrganizationRegistryMigrator(firstDataSource).MigrateAsync();
            var registry = new PostgreSqlOrganizationRegistry(firstDataSource);
            var importer = new OrganizationConfigurationImporter(
                registry,
                new ManualTimeProvider(FirstImportAt));

            imported = await importer.ImportAsync(ExampleConfiguration());
        }

        await using var secondDataSource = fixture.CreateDataSource();
        var reloaded = await new PostgreSqlOrganizationRegistry(secondDataSource)
            .FindSnapshotAsync(imported.Plan!.OrganizationId);

        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        Assert.NotNull(reloaded);
        Assert.Equal(1, reloaded.Version);
        Assert.Equal(imported.Snapshot!.Fingerprint, reloaded.Fingerprint);
        Assert.Equal(FirstImportAt, reloaded.ImportedAt);
        Assert.Equal(2, reloaded.Units.Count);
        Assert.Equal(2, reloaded.Positions.Count);
        Assert.Equal(2, reloaded.Occupants.Count);
        Assert.Equal(2, reloaded.Authorities.Count);
        var schedule = Assert.Single(reloaded.Schedules).Value.Value;
        Assert.True(schedule.IsActive);
        Assert.Equal("normal", schedule.Priority);
        Assert.False(schedule.IsCritical);
        Assert.Equal("skip", schedule.CatchUp);
        var authority = reloaded.Authorities[PositionId.From("delivery-lead")].Value;
        Assert.Equal(["delivery.bug-triage"], authority.CanDecide);
        var authorityOverride = Assert.Single(authority.Overrides);
        Assert.Equal("comms.external-official", authorityOverride.Key);
        Assert.Equal(ActionDomainGate.HumanApproval, authorityOverride.Gate);
        Assert.Equal("ceo", authorityOverride.Approver);

        await using var command = secondDataSource.CreateCommand(
            """
            SELECT configuration_version, configuration_fingerprint, imported_at
            FROM registry.organizations
            WHERE organization_id = @organization_id;
            """);
        command.Parameters.AddWithValue("organization_id", imported.Plan.OrganizationId.Value);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(1, reader.GetInt64(0));
        Assert.Equal(imported.Snapshot.Fingerprint, reader.GetString(1));
        Assert.Equal(FirstImportAt, reader.GetFieldValue<DateTimeOffset>(2));
    }

    [Fact]
    public async Task Same_fingerprint_is_a_write_free_no_op()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var clock = new ManualTimeProvider(FirstImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);
        var configuration = ExampleConfiguration();

        var first = await importer.ImportAsync(configuration);
        var before = await ReadPersistedStateAsync(dataSource, first.Plan!.OrganizationId.Value);
        clock.UtcNow = FirstImportAt.AddHours(1);

        var second = await importer.ImportAsync(configuration);
        var after = await ReadPersistedStateAsync(dataSource, first.Plan.OrganizationId.Value);

        Assert.Equal(OrganizationImportStatus.NoChanges, second.Status);
        Assert.Equal(1, second.Snapshot!.Version);
        Assert.Equal(FirstImportAt, second.Snapshot.ImportedAt);
        Assert.Empty(second.Plan!.Changes);
        Assert.Equal(before, after);
    }

    [Fact]
    public async Task Changed_configuration_updates_and_removes_only_planned_rows()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var clock = new ManualTimeProvider(FirstImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);
        var configuration = ExampleConfiguration();
        var first = await importer.ImportAsync(configuration);
        var secondImportAt = FirstImportAt.AddHours(1);
        clock.UtcNow = secondImportAt;

        var second = await importer.ImportAsync(WithRenamedDeliveryLeadAndNoSchedule(configuration));
        var reloaded = await registry.FindSnapshotAsync(configuration.Organization.Id);

        Assert.Equal(OrganizationImportStatus.Applied, second.Status);
        Assert.Equal(2, reloaded!.Version);
        Assert.Equal(secondImportAt, reloaded.ImportedAt);
        Assert.Empty(reloaded.Schedules);
        Assert.Equal(
            [
                (RegistryEntityKind.Position, "delivery-lead", RegistryChangeKind.Updated),
                (RegistryEntityKind.Schedule, "delivery-lead/relatorio-diario", RegistryChangeKind.Removed),
            ],
            second.Plan!.Changes.Select(change => (change.EntityKind, change.Key, change.Kind)));

        var ceo = PositionId.From("ceo");
        var deliveryLead = PositionId.From("delivery-lead");
        Assert.Equal(FirstImportAt, reloaded.Positions[ceo].UpdatedAt);
        Assert.Equal(secondImportAt, reloaded.Positions[deliveryLead].UpdatedAt);
        Assert.Equal("Engineering Lead", reloaded.Positions[deliveryLead].Value.Name);
        Assert.Equal(first.Snapshot!.Positions[ceo].Fingerprint, reloaded.Positions[ceo].Fingerprint);
    }

    [Fact]
    public async Task Concurrent_first_import_is_serialized()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var configuration = ExampleConfiguration();
        var firstImporter = new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(FirstImportAt));
        var secondImporter = new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(FirstImportAt));

        var results = await Task.WhenAll(
            firstImporter.ImportAsync(configuration).AsTask(),
            secondImporter.ImportAsync(configuration).AsTask());
        var reloaded = await registry.FindSnapshotAsync(configuration.Organization.Id);

        Assert.Equal(
            [OrganizationImportStatus.Applied, OrganizationImportStatus.NoChanges],
            results.Select(result => result.Status).Order().ToArray());
        Assert.Equal(1, reloaded!.Version);
        Assert.All(results, result => Assert.Equal(reloaded.Fingerprint, result.Snapshot!.Fingerprint));
    }

    [Fact]
    public async Task Reloaded_registry_serves_organization_relations()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var configuration = ExampleConfiguration();
        await new OrganizationConfigurationImporter(registry).ImportAsync(configuration);
        Hive.Domain.Organization.IOrganizationRelations relations = registry;
        var organizationId = configuration.Organization.Id;
        var ceo = PositionId.From("ceo");
        var deliveryLead = PositionId.From("delivery-lead");

        Assert.Equal(ceo, await relations.GetDirectSuperiorAsync(organizationId, deliveryLead));
        Assert.Equal(
            UnitId.From("engenharia"),
            await relations.GetUnitOfPositionAsync(organizationId, deliveryLead));
        Assert.Null(
            await relations.GetUnitOfPositionAsync(
                organizationId,
                PositionId.From("missing-position")));
        Assert.Equal(
            deliveryLead,
            Assert.Single(await relations.GetDirectSubordinatesAsync(organizationId, ceo)));
        Assert.Equal(ceo, await relations.GetRootUnitLeadershipAsync(organizationId));
        Assert.IsType<Hive.Domain.Messaging.OrganizationOwnerEndpointRef>(
            await relations.GetOrganizationOwnerAsync(organizationId));
    }

    [Fact]
    public async Task Failed_write_rolls_back_the_complete_import()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var clock = new ManualTimeProvider(FirstImportAt);
        var importer = new OrganizationConfigurationImporter(registry, clock);
        var configuration = ExampleConfiguration();
        await importer.ImportAsync(configuration);

        await using (var command = dataSource.CreateCommand(
            """
            CREATE FUNCTION registry.reject_position_update()
            RETURNS trigger
            LANGUAGE plpgsql
            AS $$
            BEGIN
                RAISE EXCEPTION 'forced registry write failure';
            END;
            $$;

            CREATE TRIGGER reject_position_update
            BEFORE UPDATE ON registry.positions
            FOR EACH ROW
            WHEN (OLD.position_id = 'delivery-lead')
            EXECUTE FUNCTION registry.reject_position_update();
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        clock.UtcNow = FirstImportAt.AddHours(1);
        var exception = await Assert.ThrowsAsync<PostgresException>(
            () => importer.ImportAsync(WithRenamedDeliveryLeadAndNoSchedule(configuration)).AsTask());
        var reloaded = await registry.FindSnapshotAsync(configuration.Organization.Id);

        Assert.Contains("forced registry write failure", exception.MessageText, StringComparison.Ordinal);
        Assert.Equal(1, reloaded!.Version);
        Assert.Equal(FirstImportAt, reloaded.ImportedAt);
        Assert.Equal(
            "Delivery Lead",
            reloaded.Positions[PositionId.From("delivery-lead")].Value.Name);
        Assert.Single(reloaded.Schedules);
    }

    private static async Task<PersistedState> ReadPersistedStateAsync(
        Npgsql.NpgsqlDataSource dataSource,
        string organizationId)
    {
        await using var command = dataSource.CreateCommand(
            """
            SELECT configuration_version,
                   configuration_fingerprint,
                   imported_at,
                   xmin::text,
                   (SELECT count(*) FROM registry.schedules s WHERE s.organization_id = o.organization_id)
            FROM registry.organizations o
            WHERE organization_id = @organization_id;
            """);
        command.Parameters.AddWithValue("organization_id", organizationId);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new PersistedState(
            reader.GetInt64(0),
            reader.GetString(1),
            reader.GetFieldValue<DateTimeOffset>(2),
            reader.GetString(3),
            reader.GetInt64(4));
    }

    private static OrganizationConfiguration ExampleConfiguration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(RepositoryRoot, "config", "organizations", "acme-delivery", "organization.yaml"));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

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

    private sealed record PersistedState(
        long Version,
        string Fingerprint,
        DateTimeOffset ImportedAt,
        string TransactionId,
        long ScheduleCount);
}
