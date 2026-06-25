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

    /// <summary>
    /// Whether the position shard region remembers its entities (US-F0-06-T04c). When
    /// <see langword="true"/> (the default), positions kept warm by an active agenda/subscription
    /// stay alive and are therefore remembered, so Cluster Sharding restarts them automatically
    /// after a rebalance or node restart; inactive positions that passivate are forgotten and
    /// reactivated on demand. Remember-entities is a durable placement contract — keep it identical
    /// on every node and do not change it while positions are persisted.
    /// </summary>
    public bool RememberEntities { get; set; } = true;

    /// <summary>
    /// Initial inactivity threshold after which an idle position is eligible for passivation
    /// (US-F0-06-T04c). When left unset (<see langword="null"/>), the workload's placement default
    /// is used. With <see cref="RememberEntities"/> enabled, Akka.NET region-driven idle passivation
    /// is disabled (it is mutually exclusive with remember-entities), so this value is the initial
    /// threshold the safe-passivation protocol (US-F0-06-T11) uses to passivate inactive positions
    /// explicitly; with remember-entities disabled, the region auto-passivates entities idle for
    /// longer than this. When set, it must be greater than zero.
    /// </summary>
    public TimeSpan? PassivateIdleAfter { get; set; }

    /// <summary>
    /// Maximum time the <c>agents</c> workload waits for the <c>ActorSystem</c> to reach
    /// cluster <em>Up</em> before initializing Cluster Sharding for the <c>PositionActor</c>
    /// (US-F0-06-T04d). Cluster Sharding must only start once this node is a full cluster member,
    /// so the workload gates its start on <em>Up</em> and fails the node observably if the system
    /// does not reach <em>Up</em> within this window. When left unset (<see langword="null"/>), the
    /// workload's placement default is used. When set, it must be greater than zero.
    /// </summary>
    public TimeSpan? ClusterUpTimeout { get; set; }
}
