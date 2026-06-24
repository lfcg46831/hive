using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies the Cluster Sharding message extractor and shard resolver for the <c>PositionActor</c>
/// (US-F0-06-T04a): entity-id extraction from the canonical <c>OrganizationId/PositionId</c> form,
/// command unwrapping, and stable, in-range shard resolution.
/// </summary>
public sealed class PositionMessageExtractorTests
{
    private static PositionEntityId EntityId(string organization, string position) =>
        PositionEntityId.From(OrganizationId.From(organization), PositionId.From(position));

    private static PositionEnvelope Envelope(string organization, string position) =>
        new(EntityId(organization, position), new RequestPassivation());

    [Fact]
    public void Default_number_of_shards_is_the_stable_contract_value()
    {
        Assert.Equal(50, PositionMessageExtractor.DefaultNumberOfShards);
        Assert.Equal(50, new PositionMessageExtractor().MaxNumberOfShards);
    }

    [Fact]
    public void Entity_id_is_the_canonical_org_slash_position_value()
    {
        var extractor = new PositionMessageExtractor();

        Assert.Equal("acme/bug-triage", extractor.EntityId(Envelope("acme", "bug-triage")));
    }

    [Fact]
    public void Entity_id_is_null_for_non_envelope_messages()
    {
        var extractor = new PositionMessageExtractor();

        Assert.Null(extractor.EntityId("not-an-envelope"));
        Assert.Null(extractor.EntityId(new RequestPassivation()));
    }

    [Fact]
    public void Entity_message_unwraps_the_command()
    {
        var extractor = new PositionMessageExtractor();
        var command = new RequestPassivation("idle");
        var envelope = new PositionEnvelope(EntityId("acme", "eng-lead"), command);

        Assert.Same(command, extractor.EntityMessage(envelope));
    }

    [Fact]
    public void Entity_message_passes_through_non_envelope_messages()
    {
        var extractor = new PositionMessageExtractor();

        Assert.Equal("other", extractor.EntityMessage("other"));
    }

    [Fact]
    public void Shard_id_of_the_envelope_matches_the_shard_id_of_its_entity_id()
    {
        var extractor = new PositionMessageExtractor();
        var entityId = EntityId("acme", "bug-triage");
        var envelope = new PositionEnvelope(entityId, new RequestPassivation());

        Assert.Equal(extractor.ShardId(entityId.Value), extractor.ShardId(envelope));
    }

    [Fact]
    public void Shard_id_is_stable_for_the_same_entity_id()
    {
        var first = new PositionMessageExtractor();
        var second = new PositionMessageExtractor();

        Assert.Equal(first.ShardId("acme/bug-triage"), second.ShardId("acme/bug-triage"));
    }

    [Theory]
    [InlineData("acme/bug-triage")]
    [InlineData("acme/eng-lead")]
    [InlineData("globex/ceo")]
    [InlineData("a/b")]
    public void Shard_id_is_within_the_configured_range(string entityId)
    {
        var extractor = new PositionMessageExtractor(8);

        var shard = int.Parse(extractor.ShardId(entityId));

        Assert.InRange(shard, 0, 7);
    }

    [Fact]
    public void Constructor_rejects_a_non_positive_shard_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositionMessageExtractor(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositionMessageExtractor(-1));
    }
}
