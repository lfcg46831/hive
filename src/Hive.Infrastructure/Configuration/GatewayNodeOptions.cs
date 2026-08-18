namespace Hive.Infrastructure.Configuration;

/// <summary>
/// Configuration for the <c>gateway</c> node workload (US-F1-05-T07). It pins the Cluster Sharding
/// placement of the AI gateway entity type — one entity per provider — and the transport timeout
/// the internal gateway API of US-F0-07-T12 uses to reach it.
/// </summary>
/// <remarks>
/// Provider resilience parameters (concurrency, window, queue, retry, circuit) are a different
/// contract and remain <c>Hive:AiGateway:Providers:&lt;providerId&gt;</c> (US-F1-05-T10). This
/// section only describes where the entities live and how long a caller waits for the hop.
/// </remarks>
public sealed class GatewayNodeOptions
{
    /// <summary>
    /// Number of shards for the AI gateway entity type. When left unset (<see langword="null"/>),
    /// the extractor's placement-contract default is used. The count must be identical on every
    /// node; the entities are not persistent, so changing it only reshuffles live provider buckets.
    /// When set, it must be greater than zero.
    /// </summary>
    public int? NumberOfShards { get; set; }

    /// <summary>
    /// Maximum time the gateway workloads wait for the <c>ActorSystem</c> to reach cluster
    /// <em>Up</em> before initializing the shard region or its proxy. When left unset
    /// (<see langword="null"/>), the workload's placement default is used. When set, it must be
    /// greater than zero.
    /// </summary>
    public TimeSpan? ClusterUpTimeout { get; set; }

    /// <summary>
    /// Transport timeout for a single gateway call routed to the provider entity. It must exceed
    /// the worst case of the resilience pipeline — queue wait plus every retry, its backoff and the
    /// provider timeout — otherwise a slow but healthy call is turned into a transport failure.
    /// When left unset (<see langword="null"/>), the invoker's default is used. When set, it must
    /// be greater than zero.
    /// </summary>
    public TimeSpan? AskTimeout { get; set; }
}
