using Akka.Actor;
using Akka.Cluster;
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
/// shard count from configuration onto the placement contract of US-F0-06-T04a, applies the
/// remember-entities and initial passivation strategy of US-F0-06-T04c, and starts idempotently so
/// repeated activation returns the same region with no side effects.
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
/// Remember-entities (US-F0-06-T04c) keeps positions that stay warm — those with an active
/// agenda/subscription — remembered, so the region restarts them after a rebalance or node restart;
/// inactive positions that passivate are forgotten and reactivated on demand. Akka.NET region-driven
/// idle passivation is mutually exclusive with remember-entities, so when remember-entities is on the
/// region never auto-passivates and the safe-passivation protocol (US-F0-06-T11) passivates inactive
/// positions explicitly using <see cref="PassivateIdleAfter"/> as the initial inactivity threshold;
/// when remember-entities is off the region auto-passivates entities idle for longer than it.
/// </para>
/// <para>
/// This workload gates the initialization on the cluster reaching <c>Up</c> within a configured
/// timeout, failing the node observably otherwise (US-F0-06-T04d). The inactivity guard rails and
/// drain/stash passivation protocol are US-F0-06-T11.
/// </para>
/// </remarks>
public sealed class PositionShardingWorkload : IRoleWorkload
{
    /// <summary>
    /// Placement default for <see cref="PassivateIdleAfter"/> when the host leaves it unset: the
    /// initial inactivity threshold for an idle position, mirroring Akka's own 120s default.
    /// </summary>
    public static readonly TimeSpan DefaultPassivateIdleAfter = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Placement default for <see cref="ClusterUpTimeout"/> when the host leaves it unset: the
    /// window the workload waits for the node to reach cluster <em>Up</em> before failing the
    /// arranque observably (US-F0-06-T04d).
    /// </summary>
    public static readonly TimeSpan DefaultClusterUpTimeout = TimeSpan.FromSeconds(30);

    private readonly ActorSystem _system;
    private readonly IPositionEntityProps _entityProps;
    private readonly int _numberOfShards;
    private readonly bool _rememberEntities;
    private readonly TimeSpan _passivateIdleAfter;
    private readonly TimeSpan _clusterUpTimeout;
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
        var agents = options.Value.Agents;
        _numberOfShards =
            agents?.NumberOfShards ?? PositionMessageExtractor.DefaultNumberOfShards;

        // Remember-entities and the initial passivation threshold (US-F0-06-T04c). Remember-entities
        // defaults on so warm positions survive rebalance/restart; the threshold falls back to the
        // placement default. A non-positive configured threshold is rejected by HiveOptionsValidator.
        _rememberEntities = agents?.RememberEntities ?? true;
        _passivateIdleAfter = agents?.PassivateIdleAfter ?? DefaultPassivateIdleAfter;

        // The cluster-up gate window (US-F0-06-T04d): how long to wait for this node to reach
        // cluster Up before failing the arranque observably. Falls back to the placement default;
        // a non-positive configured value is rejected up front by HiveOptionsValidator.
        _clusterUpTimeout = agents?.ClusterUpTimeout ?? DefaultClusterUpTimeout;
    }

    public string Role => NodeRoleNames.Agents;

    /// <summary>The started shard region, or <see langword="null"/> before the first start.</summary>
    public IActorRef? Region => _region;

    /// <summary>The shard count this workload pins onto the position placement contract.</summary>
    public int NumberOfShards => _numberOfShards;

    /// <summary>Whether the region remembers its entities (US-F0-06-T04c).</summary>
    public bool RememberEntities => _rememberEntities;

    /// <summary>The initial inactivity threshold for passivating an idle position (US-F0-06-T04c).</summary>
    public TimeSpan PassivateIdleAfter => _passivateIdleAfter;

    /// <summary>The window the workload waits for cluster <em>Up</em> before failing (US-F0-06-T04d).</summary>
    public TimeSpan ClusterUpTimeout => _clusterUpTimeout;

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

            // Ordering gate (US-F0-06-T04d): Cluster Sharding must only be initialized once this
            // node is a full cluster member, so wait for self-member Up before touching the region.
            // If the node does not reach Up within the configured window, fail the arranque
            // observably instead of starting sharding on a node that has not joined the cluster.
            await WaitForClusterUpAsync(cancellationToken).ConfigureAwait(false);

            // Resolve the extension first: accessing it injects the akka.cluster.sharding default
            // config as a top-level fallback, so ClusterShardingSettings.Create can read it even
            // when the host did not declare any sharding HOCON explicitly.
            var sharding = ClusterSharding.Get(_system);

            // Apply the remember-entities and initial passivation strategy (US-F0-06-T04c). In
            // Akka.NET region-driven idle passivation requires !RememberEntities, so the two are
            // mutually exclusive: with remember-entities on we explicitly disable region auto-idle
            // (TimeSpan.Zero) — inactive positions are passivated by the US-F0-06-T11 protocol — and
            // with it off we hand the region the configured idle threshold to auto-passivate.
            var settings = ClusterShardingSettings.Create(_system)
                .WithRole(NodeRoleNames.Agents)
                .WithRememberEntities(_rememberEntities)
                .WithPassivateIdleAfter(_rememberEntities ? TimeSpan.Zero : _passivateIdleAfter);

            _region = await sharding
                .StartAsync(
                    typeName: PositionEntityId.EntityTypeName,
                    entityPropsFactory: entityId => _entityProps.Create(entityId),
                    settings: settings,
                    messageExtractor: new PositionMessageExtractor(_numberOfShards))
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Cluster Sharding initialized for entity type {EntityType} with {ShardCount} shards on role {Role} "
                + "(rememberEntities={RememberEntities}, passivateIdleAfter={PassivateIdleAfter}).",
                PositionEntityId.EntityTypeName,
                _numberOfShards,
                NodeRoleNames.Agents,
                _rememberEntities,
                _passivateIdleAfter);
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>
    /// Waits for this node to reach cluster <em>Up</em> within <see cref="ClusterUpTimeout"/>
    /// (US-F0-06-T04d). Returns once the node is <em>Up</em>; throws
    /// <see cref="ClusterStartupTimeoutException"/> if the window elapses first, or
    /// <see cref="OperationCanceledException"/> if the host is shutting down.
    /// </summary>
    private async Task WaitForClusterUpAsync(CancellationToken cancellationToken)
    {
        var cluster = Cluster.Get(_system);

        // RegisterOnMemberUp invokes the callback immediately when the node is already Up, and
        // otherwise once it transitions to Up, so this covers both the warm and cold start paths.
        var memberUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.RegisterOnMemberUp(() => memberUp.TrySetResult());

        using var timeoutCts = new CancellationTokenSource(_clusterUpTimeout);
        using var linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var waitForTimeoutOrCancel = Task.Delay(Timeout.Infinite, linkedCts.Token);
        var completed = await Task.WhenAny(memberUp.Task, waitForTimeoutOrCancel)
            .ConfigureAwait(false);

        if (completed == memberUp.Task)
        {
            return;
        }

        // The delay completed first: either the host is shutting down (propagate cancellation) or
        // the cluster-up window elapsed (fail the arranque observably).
        cancellationToken.ThrowIfCancellationRequested();

        var lastStatus = cluster.SelfMember.Status;
        _logger.LogError(
            "Cluster Sharding for role {Role} was not initialized: the ActorSystem did not reach "
            + "cluster Up within {ClusterUpTimeout} (last self-member status: {SelfStatus}).",
            NodeRoleNames.Agents,
            _clusterUpTimeout,
            lastStatus);

        throw new ClusterStartupTimeoutException(NodeRoleNames.Agents, _clusterUpTimeout, lastStatus);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The shard region's lifecycle is bound to the ActorSystem and torn down by Akka's
        // coordinated shutdown when the host stops; there is nothing extra to do here for T04b.
        return Task.CompletedTask;
    }
}
