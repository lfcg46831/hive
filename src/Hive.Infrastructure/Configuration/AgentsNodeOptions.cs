namespace Hive.Infrastructure.Configuration;

/// <summary>
/// Configuration for the <c>agents</c> node workload (US-F0-06-T04b). It pins the durable
/// Cluster Sharding placement contract for the <c>PositionActor</c> entity type that hosts with
/// the <see cref="NodeRoleNames.Agents"/> role initialize.
/// </summary>
public sealed class AgentsNodeOptions
{
    /// <summary>
    /// Number of shards for the position entity type. When left unset (<see langword="null"/>),
    /// the sharding extractor's placement-contract default is used (US-F0-06-T04a). This count is
    /// a long-lived placement contract: it must be identical on every node and must not change
    /// while position entities are persisted, because changing it reshuffles every position across
    /// shards. When set, it must be greater than zero.
    /// </summary>
    public int? NumberOfShards { get; set; }
}
