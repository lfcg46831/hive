using Akka.Actor;
using Akka.Cluster.Sharding;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Sharding;

/// <summary>
/// Initializes Akka.Cluster Sharding for the <c>PositionActor</c> entity type on nodes that
/// declare the <see cref="NodeRoleNames.Agents"/> role (US-F0-06-T04b). It registers the shard
/// region under the stable entity type name <see cref="PositionEntityId.EntityTypeName"/>, pins the
/// shard count from configuration onto the placement contract of US-F0-06-T04a, and starts
/// idempotently so repeated activation returns the same region with no side effects.
/// </summary>
/// <remarks>
/// <para>
/// Shards are restricted to <see cref="NodeRoleNames.Agents"/> members so position entities only
/// ever allocate on agent nodes, even in an all-in-one node. The region is built from the
/// <see cref="PositionMessageExtractor"/> (entity/shard resolution and envelope unwrapping) and the
/// <see cref="IPositionEntityProps"/> seam, which later stories replace with the real persistent
/// entity (US-F0-06-T06b/T09) without changing this wiring.
/// </para>
/// <para>
/// This workload only owns the initialization mechanism. Gating the start on the cluster reaching
/// <c>Up</c> within a timeout — failing the node observably otherwise — is the ordering concern of
/// US-F0-06-T04d; passivation and remember-entities are US-F0-06-T04c.
/// </para>
/// </remarks>
public sealed class PositionShardingWorkload : IRoleWorkload
{
    private readonly ActorSystem _system;
    private readonly IPositionEntityProps _entityProps;
    private readonly int _numberOfShards;
    private readonly ILogger<PositionShardingWorkload> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private IActorRef? _region;

    public PositionShardingWorkload(
        ActorSystem system,
        IPositionEntityProps entityProps,
        IOptions<HiveOptions> options,
        ILogger<PositionShardingWorkload> logger)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _entityProps = entityProps ?? throw new ArgumentNullException(nameof(entityProps));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // Pin the durable placement contract: the configured shard count, or the extractor's
        // stable default when unset (US-F0-06-T04a). A non-positive configured value is rejected
        // up front by HiveOptionsValidator, so this resolution never produces an invalid count.
        _numberOfShards =
            options.Value.Agents?.NumberOfShards ?? PositionMessageExtractor.DefaultNumberOfShards;
    }

    public string Role => NodeRoleNames.Agents;

    /// <summary>The started shard region, or <see langword="null"/> before the first start.</summary>
    public IActorRef? Region => _region;

    /// <summary>The shard count this workload pins onto the position placement contract.</summary>
    public int NumberOfShards => _numberOfShards;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_region is not null)
            {
                // Idempotent: already initialized on this node, return without re-registering.
                return;
            }

            // Resolve the extension first: accessing it injects the akka.cluster.sharding default
            // config as a top-level fallback, so ClusterShardingSettings.Create can read it even
            // when the host did not declare any sharding HOCON explicitly.
            var sharding = ClusterSharding.Get(_system);

            var settings = ClusterShardingSettings.Create(_system)
                .WithRole(NodeRoleNames.Agents);

            _region = await sharding
                .StartAsync(
                    typeName: PositionEntityId.EntityTypeName,
                    entityPropsFactory: entityId => _entityProps.Create(entityId),
                    settings: settings,
                    messageExtractor: new PositionMessageExtractor(_numberOfShards))
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Cluster Sharding initialized for entity type {EntityType} with {ShardCount} shards on role {Role}.",
                PositionEntityId.EntityTypeName,
                _numberOfShards,
                NodeRoleNames.Agents);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The shard region's lifecycle is bound to the ActorSystem and torn down by Akka's
        // coordinated shutdown when the host stops; there is nothing extra to do here for T04b.
        return Task.CompletedTask;
    }
}
