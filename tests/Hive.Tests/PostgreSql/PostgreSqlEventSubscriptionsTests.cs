using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlEventSubscriptionsTests(PostgreSqlFixture fixture)
{
    [Fact]
    public async Task Seam_survives_reconnect_and_observes_reimport_and_removal()
    {
        await fixture.ResetRegistryAsync();
        var configuration = EventSubscriptionConfigurationTests.Configuration(
            $"[{EventSubscriptionConfigurationTests.Deadline}, {EventSubscriptionConfigurationTests.Blocked}, {EventSubscriptionConfigurationTests.Budget}]");
        OrganizationImportResult imported;
        await using (var dataSource = fixture.CreateDataSource())
        {
            await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
            imported = await new OrganizationConfigurationImporter(new PostgreSqlOrganizationRegistry(dataSource)).ImportAsync(configuration);
        }

        await using var seam = new PostgreSqlEventSubscriptions(fixture.ConnectionString);
        var old = await seam.GetSnapshotAsync(configuration.Organization.Id);
        foreach (var type in Enum.GetValues<OrganizationEventType>())
        {
            Assert.Equal(imported.Snapshot!.EventSubscriptions.Resolve(type).ToArray(), old.Resolve(type).ToArray());
            Assert.Equal(PositionId.From("ceo"), Assert.Single(old.Resolve(type)).PositionId);
        }

        await using var reconnected = fixture.CreateDataSource();
        var importer = new OrganizationConfigurationImporter(new PostgreSqlOrganizationRegistry(reconnected));
        await importer.ImportAsync(EventSubscriptionConfigurationTests.Configuration(
            $"[{EventSubscriptionConfigurationTests.Budget.Replace("80", "90")}]"));
        var updated = await seam.GetSnapshotAsync(configuration.Organization.Id);
        Assert.Empty(updated.Resolve(OrganizationEventType.DirectiveDeadlineApproaching));
        Assert.Equal(90, Assert.IsType<BudgetThresholdParameters>(Assert.Single(
            updated.Resolve(OrganizationEventType.BudgetThresholdReached)).Subscription.Parameters).ThresholdPercent);
        await importer.ImportAsync(EventSubscriptionConfigurationTests.Configuration("[]"));
        var removed = await seam.GetSnapshotAsync(configuration.Organization.Id);
        Assert.All(Enum.GetValues<OrganizationEventType>(), type => Assert.Empty(removed.Resolve(type)));
        Assert.Single(old.Resolve(OrganizationEventType.DirectiveDeadlineApproaching));
        await Assert.ThrowsAsync<EventSubscriptionsNotFoundException>(() => seam.GetSnapshotAsync(OrganizationId.From("unknown")).AsTask());
    }
}
