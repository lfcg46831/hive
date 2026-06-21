using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

/// <summary>
/// Tests for the materialized read-only relations service and its snapshot builder
/// (US-F0-04-T02). The behavior under test is the same contract documented by
/// <see cref="IOrganizationRelations"/> (exercised by <see cref="OrganizationRelationsContractTests"/>
/// through an in-test fake), here served by the real <see cref="MaterializedOrganizationRelations"/>
/// over an in-memory <see cref="OrganizationRelationsSnapshot"/>, plus the internal-consistency
/// guards of the snapshot builder.
/// </summary>
public sealed class MaterializedOrganizationRelationsTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");

    [Fact]
    public async Task Root_unit_leadership_has_no_direct_superior()
    {
        var relations = SampleService();

        var superior = await relations.GetDirectSuperiorAsync(Org, Position("ceo"));

        Assert.Null(superior);
    }

    [Fact]
    public async Task Direct_superior_is_resolved_for_a_subordinate()
    {
        var relations = SampleService();

        var superior = await relations.GetDirectSuperiorAsync(Org, Position("engineer"));

        Assert.Equal(Position("delivery-lead"), superior);
    }

    [Fact]
    public async Task Direct_subordinates_are_returned_in_declaration_order_without_duplicates()
    {
        var relations = SampleService();

        var subordinates = await relations.GetDirectSubordinatesAsync(Org, Position("delivery-lead"));

        Assert.Equal(new[] { Position("engineer"), Position("qa") }, subordinates);
    }

    [Fact]
    public async Task A_leaf_position_has_no_subordinates()
    {
        var relations = SampleService();

        var subordinates = await relations.GetDirectSubordinatesAsync(Org, Position("engineer"));

        Assert.Empty(subordinates);
    }

    [Fact]
    public async Task Root_unit_leadership_is_the_top_position()
    {
        var relations = SampleService();

        var leader = await relations.GetRootUnitLeadershipAsync(Org);

        Assert.Equal(Position("ceo"), leader);
    }

    [Fact]
    public async Task Organization_owner_is_the_configured_routing_endpoint()
    {
        var owner = new OrganizationOwnerEndpointRef();
        var relations = new MaterializedOrganizationRelations(BuildSample(owner));

        var resolved = await relations.GetOrganizationOwnerAsync(Org);

        Assert.Same(owner, resolved);
    }

    [Fact]
    public async Task Unit_membership_is_resolved_for_a_known_position()
    {
        var relations = SampleService();

        var unit = await relations.GetUnitOfPositionAsync(Org, Position("engineer"));

        Assert.Equal(UnitId.From("delivery"), unit);
    }

    [Fact]
    public async Task Unit_membership_probe_returns_null_for_an_unknown_position()
    {
        var relations = SampleService();

        var unit = await relations.GetUnitOfPositionAsync(Org, Position("ghost"));

        Assert.Null(unit);
    }

    [Fact]
    public async Task Superior_query_throws_for_an_unknown_position()
    {
        var relations = SampleService();

        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(
            async () => await relations.GetDirectSuperiorAsync(Org, Position("ghost")));
    }

    [Fact]
    public async Task Subordinates_query_throws_for_an_unknown_position()
    {
        var relations = SampleService();

        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(
            async () => await relations.GetDirectSubordinatesAsync(Org, Position("ghost")));
    }

    [Fact]
    public async Task Queries_throw_for_an_unknown_organization()
    {
        var relations = SampleService();

        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(
            async () => await relations.GetRootUnitLeadershipAsync(OrganizationId.From("other-org")));
    }

    [Fact]
    public async Task Snapshots_are_resolved_per_organization_without_leaking_structure()
    {
        var other = OrganizationId.From("other-org");
        var relations = new MaterializedOrganizationRelations(new[]
        {
            BuildSample(new OrganizationOwnerEndpointRef()),
            OrganizationRelationsSnapshot
                .CreateBuilder(other, new OrganizationOwnerEndpointRef())
                .AddPosition(Position("founder"), UnitId.From("root"))
                .Build(),
        });

        Assert.Equal(Position("ceo"), await relations.GetRootUnitLeadershipAsync(Org));
        Assert.Equal(Position("founder"), await relations.GetRootUnitLeadershipAsync(other));

        // A position known only in the other organization is unknown here.
        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(
            async () => await relations.GetDirectSuperiorAsync(Org, Position("founder")));
    }

    [Fact]
    public void Two_snapshots_for_the_same_organization_are_rejected()
    {
        Assert.Throws<ArgumentException>(() => new MaterializedOrganizationRelations(new[]
        {
            BuildSample(new OrganizationOwnerEndpointRef()),
            BuildSample(new OrganizationOwnerEndpointRef()),
        }));
    }

    [Fact]
    public void An_empty_snapshot_cannot_be_built()
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(Org, new OrganizationOwnerEndpointRef());

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void A_position_cannot_be_added_twice()
    {
        var builder = OrganizationRelationsSnapshot
            .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
            .AddPosition(Position("ceo"), UnitId.From("root"));

        Assert.Throws<ArgumentException>(
            () => builder.AddPosition(Position("ceo"), UnitId.From("root")));
    }

    [Fact]
    public void A_position_cannot_be_its_own_superior()
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(Org, new OrganizationOwnerEndpointRef());

        Assert.Throws<ArgumentException>(
            () => builder.AddPosition(Position("ceo"), UnitId.From("root"), Position("ceo")));
    }

    [Fact]
    public void A_superior_must_reference_a_known_position()
    {
        var builder = OrganizationRelationsSnapshot
            .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
            .AddPosition(Position("ceo"), UnitId.From("root"))
            .AddPosition(Position("engineer"), UnitId.From("delivery"), Position("ghost"));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void Exactly_one_root_unit_leadership_is_required()
    {
        var builder = OrganizationRelationsSnapshot
            .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
            .AddPosition(Position("ceo"), UnitId.From("root"))
            .AddPosition(Position("coo"), UnitId.From("root"));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    [Fact]
    public void A_cycle_in_the_superior_edges_is_rejected()
    {
        var builder = OrganizationRelationsSnapshot
            .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
            .AddPosition(Position("a"), UnitId.From("root"), Position("b"))
            .AddPosition(Position("b"), UnitId.From("root"), Position("a"));

        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }

    private static PositionId Position(string value) => PositionId.From(value);

    private static MaterializedOrganizationRelations SampleService() =>
        new(BuildSample(new OrganizationOwnerEndpointRef()));

    /// <summary>
    /// CEO (root unit leadership) → delivery-lead → { engineer, qa }, with the CEO in the root unit
    /// and the rest in the delivery unit.
    /// </summary>
    private static OrganizationRelationsSnapshot BuildSample(OrganizationOwnerEndpointRef owner) =>
        OrganizationRelationsSnapshot
            .CreateBuilder(Org, owner)
            .AddPosition(Position("ceo"), UnitId.From("root"))
            .AddPosition(Position("delivery-lead"), UnitId.From("delivery"), Position("ceo"))
            .AddPosition(Position("engineer"), UnitId.From("delivery"), Position("delivery-lead"))
            .AddPosition(Position("qa"), UnitId.From("delivery"), Position("delivery-lead"))
            .Build();
}
