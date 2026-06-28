using Hive.Domain.Identity;
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

        var locations = await fixture.GetEntityLocationsAsync();
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
}
