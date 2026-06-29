using Hive.Domain.Identity;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class PositionShardingMultiNodeFixtureTests
{
    [Fact]
    public async Task Fixture_forms_role_cluster_and_distributes_position_entities_by_entity_id()
    {
        await using var fixture = await PositionShardingMultiNodeFixture.StartAsync();

        Assert.Equal(3, fixture.Nodes.Count);
        Assert.Equal(2, fixture.AgentNodes.Count);
        var apiNode = Assert.Single(fixture.Nodes.Where(node => node.HasRole(NodeRoleNames.Api)));
        Assert.Null(apiNode.Region);
        Assert.All(fixture.AgentNodes, node => Assert.NotNull(node.Region));

        var entities = PositionShardingMultiNodeFixture
            .GenerateEntitiesCoveringShards(OrganizationId.From("acme"), "multi-node", shardCount: 8)
            .Take(8)
            .ToArray();

        await fixture.ActivateAsync(entities);

        var locations = await fixture.WaitForAllEntitiesLocatedAsync(entities);
        var locatedEntities = locations
            .SelectMany(location => location.EntityIds)
            .ToHashSet(StringComparer.Ordinal);

        foreach (var entity in entities)
        {
            Assert.Contains(entity.Value, locatedEntities);
        }

        Assert.Equal(fixture.AgentNodes.Count, locations.Count);
        Assert.Contains(locations, location => location.EntityIds.Count > 0);
    }

    [Fact]
    public async Task Routing_by_entity_id_keeps_one_active_entity_when_sent_from_each_region()
    {
        await using var fixture = await PositionShardingMultiNodeFixture.StartAsync();
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("routing-target"));

        await fixture.SendThroughAllAgentRegionsAsync(entity, "t14b-routing");

        var locations = await fixture.GetEntityLocationsAsync();

        AssertEntityActiveExactlyOnce(entity, locations);
        Assert.All(
            fixture.AgentNodes
                .SelectMany(node => node.Publisher.Events)
                .OfType<PositionEventCommitted>()
                .Where(committed => committed.Event is ShortMemoryUpdated updated
                    && updated.Key.StartsWith("t14b-routing", StringComparison.Ordinal)),
            committed => Assert.Equal(entity, committed.EntityId));
    }

    [Fact]
    public async Task Rebalance_between_agent_regions_keeps_each_entity_active_once()
    {
        await using var fixture = await PositionShardingMultiNodeFixture.StartAsync(
            startAllAgentRegions: false);
        var entities = PositionShardingMultiNodeFixture
            .GenerateEntitiesCoveringShards(OrganizationId.From("acme"), "rebalance", shardCount: 8)
            .Take(8)
            .ToArray();

        await fixture.ActivateAsync(entities);

        Assert.NotNull(fixture.AgentNodes[0].Region);
        Assert.Null(fixture.AgentNodes[1].Region);
        Assert.Equal(
            entities.Length,
            fixture.AgentNodes[0].Publisher.Events
                .OfType<PositionEventCommitted>()
                .Count(committed => committed.Event is ShortMemoryUpdated updated
                    && updated.Key == "t14a-fixture"));

        await fixture.StartRemainingAgentShardRegionsAsync();

        var locations = await fixture.WaitForRebalancedLocationsAsync(entities);

        Assert.Contains(
            locations,
            location => location.NodeName == fixture.AgentNodes[1].Name
                && location.EntityIds.Count > 0);
        foreach (var entity in entities)
        {
            AssertEntityActiveExactlyOnce(entity, locations);
        }
    }

    private static void AssertEntityActiveExactlyOnce(
        PositionEntityId entity,
        IReadOnlyCollection<PositionEntityLocation> locations)
    {
        var owners = locations
            .Where(location => location.EntityIds.Contains(entity.Value, StringComparer.Ordinal))
            .Select(location => location.NodeName)
            .ToArray();

        var owner = Assert.Single(owners);
        Assert.False(string.IsNullOrWhiteSpace(owner));
    }
}
