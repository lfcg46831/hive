using Hive.Domain.Organization;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using static Hive.Tests.UnitLeadershipTests;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlUnitLeadershipTests(PostgreSqlFixture fixture)
{
    private static readonly Hive.Domain.Identity.OrganizationId Org = UnitLeadershipTests.Org;

    [Fact]
    public async Task Leadership_survives_reconnection_updates_and_removal_with_the_same_query_contract()
    {
        await fixture.ResetRegistryAsync();
        await using (var dataSource = fixture.CreateDataSource())
        {
            await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
            var importer = new OrganizationConfigurationImporter(new PostgreSqlOrganizationRegistry(dataSource));
            Assert.Equal(OrganizationImportStatus.Applied, (await importer.ImportAsync(Configuration())).Status);
            Assert.Equal(OrganizationImportStatus.Applied,
                (await importer.ImportAsync(Configuration(leader: "second", organization: "other"))).Status);
        }

        await using (var relations = new PostgreSqlOrganizationRelations(fixture.ConnectionString))
        {
            Assert.Equal(Ceo, await relations.GetUnitLeadershipAsync(Org, Root));
            Assert.Equal(await relations.GetRootUnitLeadershipAsync(Org), await relations.GetUnitLeadershipAsync(Org, Root));
            Assert.Equal(First, await relations.GetUnitLeadershipAsync(Org, Team));
            Assert.Equal(Second, await relations.GetUnitLeadershipAsync(Configuration(organization: "other").Organization.Id, Team));
            await AssertLookupErrorsAsync(relations);
        }

        await using (var dataSource = fixture.CreateDataSource())
        {
            var registry = new PostgreSqlOrganizationRegistry(dataSource);
            var importer = new OrganizationConfigurationImporter(registry);
            var previous = await registry.FindSnapshotAsync(Org);
            var changed = await importer.ImportAsync(Configuration(leader: "second"));
            Assert.Equal(OrganizationImportStatus.Applied, changed.Status);
            Assert.NotEqual(previous!.Relations.Fingerprint, changed.Snapshot!.Relations.Fingerprint);
            Assert.Equal(First, previous.Relations.Value.GetUnitLeadership(Team));
            Assert.Equal(OrganizationImportStatus.NoChanges,
                (await importer.ImportAsync(Configuration(leader: "second"))).Status);
        }

        await using (var dataSource = fixture.CreateDataSource())
        {
            var registry = new PostgreSqlOrganizationRegistry(dataSource);
            Assert.Equal(Second, await registry.GetUnitLeadershipAsync(Org, Team));
            var removed = await new OrganizationConfigurationImporter(registry).ImportAsync(Configuration(includeTeam: false));
            Assert.Equal(OrganizationImportStatus.Applied, removed.Status);
        }

        await using (var relations = new PostgreSqlOrganizationRelations(fixture.ConnectionString))
        {
            await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(async () =>
                await relations.GetUnitLeadershipAsync(Org, Team));
            Assert.Equal(Ceo, await relations.GetUnitLeadershipAsync(Org, Root));
        }
    }
}
