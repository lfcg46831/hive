using Akka.Cluster.Sharding;

namespace Hive.Actors.Gateway;

/// <summary>
/// Cluster Sharding message extractor for the AI gateway entity (US-F1-05-T07). It maps an
/// <see cref="AiGatewayEnvelope"/> to the sharded <c>entityId</c> (the provider key), the shard id
/// and the unwrapped command handed to the entity.
/// </summary>
/// <remarks>
/// Only envelopes are routable: <see cref="EntityId"/> returns <see langword="null"/> for anything
/// else, so an unaddressed message is dropped rather than delivered to an arbitrary provider
/// entity. The shard count is a placement contract — identical on every node — but the entities are
/// not persistent, so changing it only reshuffles live provider buckets.
/// </remarks>
public sealed class AiGatewayMessageExtractor : HashCodeMessageExtractor
{
    /// <summary>
    /// The default, stable number of shards for the gateway entity type. Providers are few, so the
    /// count only has to exceed the number of <c>gateway</c> nodes with headroom.
    /// </summary>
    public const int DefaultNumberOfShards = 12;

    public AiGatewayMessageExtractor()
        : this(DefaultNumberOfShards)
    {
    }

    public AiGatewayMessageExtractor(int numberOfShards)
        : base(RequirePositive(numberOfShards))
    {
    }

    public override string? EntityId(object message) =>
        message is AiGatewayEnvelope envelope ? envelope.ProviderKey : null;

    public override object EntityMessage(object message) =>
        message is AiGatewayEnvelope envelope ? envelope.Command : message;

    private static int RequirePositive(int numberOfShards)
    {
        if (numberOfShards <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(numberOfShards),
                numberOfShards,
                "The number of shards must be greater than zero.");
        }

        return numberOfShards;
    }
}
