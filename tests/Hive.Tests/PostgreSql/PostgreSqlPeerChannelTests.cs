using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlPeerChannelTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Channels_survive_new_connections_and_support_no_op_update_rejection_and_removal()
    {
        await fixture.ResetRegistryAsync();
        var channel = PeerChannelConfigurationTests.Channel;
        var configuration = PeerChannelConfigurationTests.Configuration($"[{channel}]");
        OrganizationImportResult first;
        await using var runtimeContracts = new PostgreSqlPeerChannelContracts(fixture.ConnectionString);
        await using (var dataSource = fixture.CreateDataSource())
        {
            await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
            first = await new OrganizationConfigurationImporter(new PostgreSqlOrganizationRegistry(dataSource))
                .ImportAsync(configuration);
            Assert.Equal(OrganizationImportStatus.Applied, first.Status);
        }

        await using (var dataSource = fixture.CreateDataSource())
        {
            var registry = new PostgreSqlOrganizationRegistry(dataSource);
            var snapshot = await registry.FindSnapshotAsync(configuration.Organization.Id);
            Assert.NotNull(snapshot);
            Assert.Equal(first.Snapshot!.Fingerprint, snapshot.Fingerprint);
            var persisted = Assert.Single(snapshot.Units[UnitId.From("root")].Value.AllowedPeerChannels);
            Assert.Equal(UnitId.From("engineering"), persisted.From);
            Assert.Equal([PeerChannelMessageType.PeerRequest, PeerChannelMessageType.Memo], persisted.Types);
            Assert.Equal(2, persisted.MaxOpenRequests);
            Assert.Equal(PeerChannelRejectionAction.Escalate, persisted.OnRejection);
            Assert.Empty(snapshot.Units[UnitId.From("engineering")].Value.AllowedPeerChannels);
            var contracts = new RegistryPeerChannelContracts(registry);
            var resolved = await contracts.ResolveChannelAsync(configuration.Organization.Id, UnitId.From("engineering"), UnitId.From("root"));
            Assert.Equal(persisted.Types, resolved!.Types);
            Assert.Equal(2, resolved.MaxOpenRequests);
            Assert.Equal(2, (await runtimeContracts.ResolveChannelAsync(configuration.Organization.Id,
                UnitId.From("engineering"), UnitId.From("root")))!.MaxOpenRequests);
            Assert.Equal(PeerChannelRejectionAction.Escalate, resolved.OnRejection);
            Assert.Null(await contracts.ResolveChannelAsync(configuration.Organization.Id, UnitId.From("root"), UnitId.From("engineering")));

            var importer = new OrganizationConfigurationImporter(registry);
            var noOp = await importer.ImportAsync(PeerChannelConfigurationTests.Configuration(
                $"[{channel.Replace("peer-request, memo", "memo, peer-request")}]"));
            Assert.Equal(OrganizationImportStatus.NoChanges, noOp.Status);
            Assert.Equal(1, noOp.Snapshot!.Version);
            var invalid = await importer.ImportAsync(PeerChannelConfigurationTests.Configuration(
                $"[{channel.Replace("engineering", "missing")}]"));
            Assert.Equal(OrganizationImportStatus.Invalid, invalid.Status);
            Assert.Equal(snapshot.Fingerprint, (await registry.FindSnapshotAsync(configuration.Organization.Id))!.Fingerprint);
            var updated = await importer.ImportAsync(PeerChannelConfigurationTests.Configuration(
                $"[{channel.Replace("max_open_requests: 2", "max_open_requests: 5").Replace("escalate", "none")}]"));
            Assert.Equal(OrganizationImportStatus.Applied, updated.Status);
            Assert.Equal(2, updated.Snapshot!.Version);
        }

        await using (var dataSource = fixture.CreateDataSource())
        {
            var registry = new PostgreSqlOrganizationRegistry(dataSource);
            var snapshot = await registry.FindSnapshotAsync(configuration.Organization.Id);
            var persisted = Assert.Single(snapshot!.Units[UnitId.From("root")].Value.AllowedPeerChannels);
            Assert.Equal(5, persisted.MaxOpenRequests);
            Assert.Equal(5, (await runtimeContracts.ResolveChannelAsync(configuration.Organization.Id,
                UnitId.From("engineering"), UnitId.From("root")))!.MaxOpenRequests);
            Assert.Equal(PeerChannelRejectionAction.None, persisted.OnRejection);
            var resolved = await new RegistryPeerChannelContracts(registry)
                .ResolveChannelAsync(configuration.Organization.Id, UnitId.From("engineering"), UnitId.From("root"));
            Assert.Equal(5, resolved!.MaxOpenRequests);
            Assert.Equal(PeerChannelRejectionAction.None, resolved.OnRejection);
            var removed = await new OrganizationConfigurationImporter(registry)
                .ImportAsync(PeerChannelConfigurationTests.Configuration());
            Assert.Equal(OrganizationImportStatus.Applied, removed.Status);
            Assert.Equal(3, removed.Snapshot!.Version);
        }

        await using (var dataSource = fixture.CreateDataSource())
        {
            var registry = new PostgreSqlOrganizationRegistry(dataSource);
            var snapshot = await registry.FindSnapshotAsync(configuration.Organization.Id);
            Assert.Empty(snapshot!.Units[UnitId.From("root")].Value.AllowedPeerChannels);
            Assert.Null(await runtimeContracts.ResolveChannelAsync(configuration.Organization.Id,
                UnitId.From("engineering"), UnitId.From("root")));
            Assert.Null(await new RegistryPeerChannelContracts(registry)
                .ResolveChannelAsync(configuration.Organization.Id, UnitId.From("engineering"), UnitId.From("root")));
        }
    }

    [Fact]
    public async Task Migration_defaults_existing_units_to_empty_channels_and_is_repeatable()
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        var migrator = new PostgreSqlOrganizationRegistryMigrator(dataSource);
        await migrator.MigrateAsync();
        var configuration = PeerChannelConfigurationTests.Configuration();
        var registry = new PostgreSqlOrganizationRegistry(dataSource);
        var first = await new OrganizationConfigurationImporter(registry).ImportAsync(configuration);
        Assert.Equal(OrganizationImportStatus.Applied, first.Status);

        // Restore the pre-008 unit table shape while retaining the imported rows.
        await using (var command = dataSource.CreateCommand("""
            ALTER TABLE registry.units DROP COLUMN allowed_peer_channels;
            DELETE FROM registry.schema_migrations WHERE version = 8;
            """))
        {
            await command.ExecuteNonQueryAsync();
        }

        await migrator.MigrateAsync();
        await migrator.MigrateAsync();
        var snapshot = await registry.FindSnapshotAsync(configuration.Organization.Id);
        Assert.Equal(first.Snapshot!.Fingerprint, snapshot!.Fingerprint);
        Assert.Equal(3, snapshot.Units.Count);
        Assert.All(snapshot.Units.Values, unit => Assert.Empty(unit.Value.AllowedPeerChannels));
        await using var count = dataSource.CreateCommand(
            "SELECT count(*) FROM registry.schema_migrations WHERE version = 8;");
        Assert.Equal(1L, await count.ExecuteScalarAsync());
    }
}
