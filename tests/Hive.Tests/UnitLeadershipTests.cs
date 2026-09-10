using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Npgsql;

namespace Hive.Tests;

public sealed class UnitLeadershipTests
{
    internal static readonly OrganizationId Org = OrganizationId.From("leadership-test");
    internal static readonly UnitId Root = UnitId.From("root");
    internal static readonly UnitId Team = UnitId.From("team");
    internal static readonly PositionId Ceo = PositionId.From("ceo");
    internal static readonly PositionId First = PositionId.From("first");
    internal static readonly PositionId Second = PositionId.From("second");

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Leadership_is_scoped_and_distinguishes_structural_errors_from_the_position_probe(bool registryBacked)
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var initial = await importer.ImportAsync(Configuration());
        var otherConfiguration = Configuration(leader: "second", organization: "other");
        var other = await importer.ImportAsync(otherConfiguration);
        Assert.Equal(OrganizationImportStatus.Applied, initial.Status);
        Assert.Equal(OrganizationImportStatus.Applied, other.Status);
        IOrganizationRelations relations = registryBacked
            ? registry
            : new MaterializedOrganizationRelations([initial.Snapshot!.Relations.Value, other.Snapshot!.Relations.Value]);

        Assert.Equal(Ceo, await relations.GetUnitLeadershipAsync(Org, Root));
        Assert.Equal(await relations.GetRootUnitLeadershipAsync(Org), await relations.GetUnitLeadershipAsync(Org, Root));
        Assert.Equal(First, await relations.GetUnitLeadershipAsync(Org, Team));
        Assert.Equal(Second, await relations.GetUnitLeadershipAsync(otherConfiguration.Organization.Id, Team));
        await AssertLookupErrorsAsync(relations);
    }

    internal static async Task AssertLookupErrorsAsync(IOrganizationRelations relations)
    {
        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(async () =>
            await relations.GetUnitLeadershipAsync(OrganizationId.From("missing"), Team));
        foreach (var unit in new[] { "missing", "Team" })
        {
            await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(async () =>
                await relations.GetUnitLeadershipAsync(Org, UnitId.From(unit)));
        }
        Assert.Null(await relations.GetUnitOfPositionAsync(Org, PositionId.From("missing")));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await relations.GetUnitLeadershipAsync(Org, Team, cancelled.Token));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await relations.GetUnitLeadershipAsync(Org, null!));
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await relations.GetUnitLeadershipAsync(null!, Team));
    }

    [Fact]
    public async Task Leadership_only_import_changes_refresh_relations_and_removal_does_not_leave_stale_units()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var first = await importer.ImportAsync(Configuration());
        var changed = await importer.ImportAsync(Configuration(leader: "second"));
        Assert.Equal(OrganizationImportStatus.Applied, changed.Status);
        Assert.Contains(changed.Plan!.Changes, change => change.EntityKind == RegistryEntityKind.CommandRelations);
        Assert.DoesNotContain(changed.Plan.Changes, change => change.EntityKind == RegistryEntityKind.Position);
        Assert.NotEqual(first.Snapshot!.Relations.Fingerprint, changed.Snapshot!.Relations.Fingerprint);
        Assert.Equal(First, first.Snapshot.Relations.Value.GetUnitLeadership(Team));
        Assert.Equal(Second, await registry.GetUnitLeadershipAsync(Org, Team));
        Assert.Equal(OrganizationImportStatus.NoChanges, (await importer.ImportAsync(Configuration(leader: "second"))).Status);
        var removed = await importer.ImportAsync(Configuration(includeTeam: false));
        Assert.Equal(OrganizationImportStatus.Applied, removed.Status);
        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(async () =>
            await registry.GetUnitLeadershipAsync(Org, Team));
        Assert.Equal(Ceo, await registry.GetUnitLeadershipAsync(Org, Root));
    }

    [Fact]
    public void Builder_requires_explicit_non_root_leadership_and_rejects_invalid_declarations()
    {
        Assert.Throws<InvalidOperationException>(() => Builder().Build());
        foreach (var leader in new[] { Ceo, PositionId.From("missing") })
        {
            Assert.Throws<InvalidOperationException>(() => Builder().AddUnitLeadership(Team, leader).Build());
        }
        Assert.Throws<InvalidOperationException>(() => Builder()
            .AddUnitLeadership(Team, First).AddUnitLeadership(Root, First).Build());
        Assert.Throws<InvalidOperationException>(() => Builder()
            .AddUnitLeadership(Team, First).AddUnitLeadership(UnitId.From("missing"), First).Build());
        var builder = Builder().AddUnitLeadership(Team, First);
        Assert.Throws<ArgumentException>(() => builder.AddUnitLeadership(Team, Second));
        Assert.Equal(First, builder.Build().GetUnitLeadership(Team));
    }

    [Fact]
    public void Builder_changes_cannot_mutate_previous_leadership_snapshots()
    {
        var builder = Builder().AddUnitLeadership(Team, Second);
        var before = builder.Build();
        var extraUnit = UnitId.From("extra");
        var extraLeader = PositionId.From("extra-leader");
        builder.AddPosition(extraLeader, extraUnit, Ceo).AddUnitLeadership(extraUnit, extraLeader);
        var after = builder.Build();
        Assert.Throws<OrganizationRelationNotFoundException>(() => before.GetUnitLeadership(extraUnit));
        Assert.Equal(extraLeader, after.GetUnitLeadership(extraUnit));
        Assert.Equal(Second, before.GetUnitLeadership(Team));
    }

    [Fact]
    public async Task Unavailable_registry_is_a_technical_failure_and_preserves_cancellation()
    {
        IOrganizationRelations relations = new UnavailableOrganizationRelations("registry");
        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await relations.GetUnitLeadershipAsync(Org, Team));
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await relations.GetUnitLeadershipAsync(Org, Team, cancelled.Token));
    }

    private static OrganizationRelationsSnapshot.Builder Builder() =>
        OrganizationRelationsSnapshot.CreateBuilder(Org, new OrganizationOwnerEndpointRef())
            .AddPosition(Ceo, Root)
            .AddPosition(First, Team, Ceo)
            .AddPosition(Second, Team, First);

    [Fact]
    public async Task PostgreSql_reader_propagates_technical_failure_instead_of_reporting_a_missing_unit()
    {
        var dataSource = NpgsqlDataSource.Create("Host=localhost;Database=unused;Username=unused");
        IOrganizationRelations relations = new PostgreSqlOrganizationRegistry(dataSource);
        await dataSource.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(async () =>
            await relations.GetUnitLeadershipAsync(Org, Team));
    }

    internal static OrganizationConfiguration Configuration(
        string leader = "first", bool includeTeam = true, string organization = "leadership-test")
    {
        var parsed = new OrganizationConfigurationParser().Parse($$"""
            organization:
              id: {{organization}}
              root_unit: root
              owner: { type: human, ref: owner@example.test }
            units:
              - id: root
                parent: null
                leadership: ceo
              - id: team
                parent: root
                leadership: {{leader}}
            positions:
              - id: ceo
                unit: root
                reports_to: null
                occupant: { type: human }
              - id: first
                unit: team
                reports_to: ceo
                occupant: { type: human }
              - id: second
                unit: team
                reports_to: ceo
                occupant: { type: human }
            """, "unit-leadership.yaml");
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Errors));
        var configuration = parsed.Configuration!;
        return includeTeam ? configuration : new OrganizationConfiguration(
            configuration.Organization,
            configuration.Units.Where(unit => unit.Id == Root).ToArray(),
            configuration.Positions.Where(position => position.Id == Ceo).ToArray());
    }
}
