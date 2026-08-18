using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Hive.Actors.Sharding;
using Hive.Domain.Ai;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Gateway;

/// <summary>
/// Materializes the AI gateway entity type on nodes that declare the <c>gateway</c> role
/// (US-F1-05-T07). Shards are restricted to that role, so a provider's queue, rate limiter and
/// circuit breaker are hosted by exactly one node at a time even when the role is scaled out.
/// </summary>
/// <remarks>
/// The entities are not persistent and hold only in-process resilience state, so remember-entities
/// is off and region-driven idle passivation is disabled: an active provider must not lose its
/// window, queue and circuit between calls. Like the position region, the workload gates its start
/// on the node reaching cluster <em>Up</em> and fails the node observably otherwise.
/// </remarks>
public sealed class AiGatewayShardingWorkload : IRoleWorkload
{
    /// <summary>Placement default for <see cref="ClusterUpTimeout"/> when the host leaves it unset.</summary>
    public static readonly TimeSpan DefaultClusterUpTimeout = TimeSpan.FromSeconds(30);

    private readonly ActorSystem _system;
    private readonly IAiGateway _gateway;
    private readonly AiGatewayShardRegion _region;
    private readonly int _numberOfShards;
    private readonly TimeSpan _clusterUpTimeout;
    private readonly ILogger<AiGatewayShardingWorkload> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private IActorRef? _started;

    public AiGatewayShardingWorkload(
        ActorSystem system,
        IAiGateway gateway,
        AiGatewayShardRegion region,
        IOptions<HiveOptions> options,
        ILogger<AiGatewayShardingWorkload> logger)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _region = region ?? throw new ArgumentNullException(nameof(region));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var gatewayOptions = options.Value.Gateway;
        _numberOfShards =
            gatewayOptions?.NumberOfShards ?? AiGatewayMessageExtractor.DefaultNumberOfShards;
        _clusterUpTimeout = gatewayOptions?.ClusterUpTimeout ?? DefaultClusterUpTimeout;
    }

    public string Role => NodeRoleNames.Gateway;

    /// <summary>The started shard region, or <see langword="null"/> before the first start.</summary>
    public IActorRef? Region => _started;

    /// <summary>The shard count this workload pins onto the gateway placement contract.</summary>
    public int NumberOfShards => _numberOfShards;

    /// <summary>The window the workload waits for cluster <em>Up</em> before failing.</summary>
    public TimeSpan ClusterUpTimeout => _clusterUpTimeout;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_started is not null)
            {
                return;
            }

            await AkkaClusterUpGate
                .WaitAsync(_system, NodeRoleNames.Gateway, _clusterUpTimeout, _logger, cancellationToken)
                .ConfigureAwait(false);

            var sharding = ClusterSharding.Get(_system);
            var settings = ClusterShardingSettings.Create(_system)
                .WithRole(NodeRoleNames.Gateway)
                .WithRememberEntities(false)
                .WithPassivateIdleAfter(TimeSpan.Zero);

            _started = await sharding
                .StartAsync(
                    typeName: AiGatewayEntityId.EntityTypeName,
                    entityPropsFactory: _ => AiGatewayActor.Props(_gateway, _logger),
                    settings: settings,
                    messageExtractor: new AiGatewayMessageExtractor(_numberOfShards))
                .ConfigureAwait(false);
            _region.Publish(_system, _started);

            _logger.LogInformation(
                "Cluster Sharding initialized for entity type {EntityType} with {ShardCount} shards on role {Role}.",
                AiGatewayEntityId.EntityTypeName,
                _numberOfShards,
                NodeRoleNames.Gateway);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The shard region's lifecycle is bound to the ActorSystem and torn down by Akka's
        // coordinated shutdown; in-flight calls are canceled by the entity's PostStop.
        return Task.CompletedTask;
    }
}

/// <summary>
/// Materializes the region proxy on <c>agents</c> nodes that do not host the gateway region, so a
/// position can reach the provider entity wherever it lives (US-F1-05-T07). On an all-in-one node
/// the region itself is the route and this workload is a no-op.
/// </summary>
public sealed class AiGatewayShardProxyWorkload : IRoleWorkload
{
    private readonly ActorSystem _system;
    private readonly AiGatewayShardRegion _region;
    private readonly ActiveNodeRoles _activeRoles;
    private readonly int _numberOfShards;
    private readonly TimeSpan _clusterUpTimeout;
    private readonly ILogger<AiGatewayShardProxyWorkload> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private IActorRef? _proxy;

    public AiGatewayShardProxyWorkload(
        ActorSystem system,
        AiGatewayShardRegion region,
        ActiveNodeRoles activeRoles,
        IOptions<HiveOptions> options,
        ILogger<AiGatewayShardProxyWorkload> logger)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _activeRoles = activeRoles ?? throw new ArgumentNullException(nameof(activeRoles));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var gatewayOptions = options.Value.Gateway;
        _numberOfShards =
            gatewayOptions?.NumberOfShards ?? AiGatewayMessageExtractor.DefaultNumberOfShards;
        _clusterUpTimeout =
            gatewayOptions?.ClusterUpTimeout ?? AiGatewayShardingWorkload.DefaultClusterUpTimeout;
    }

    public string Role => NodeRoleNames.Agents;

    /// <summary>The started region proxy, or <see langword="null"/> when this node hosts the region.</summary>
    public IActorRef? Proxy => _proxy;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_proxy is not null || _activeRoles.Contains(NodeRoleNames.Gateway))
            {
                // Colocated topology: the hosted region is already the route for this node.
                return;
            }

            await AkkaClusterUpGate
                .WaitAsync(_system, NodeRoleNames.Agents, _clusterUpTimeout, _logger, cancellationToken)
                .ConfigureAwait(false);

            _proxy = await ClusterSharding.Get(_system)
                .StartProxyAsync(
                    typeName: AiGatewayEntityId.EntityTypeName,
                    role: NodeRoleNames.Gateway,
                    messageExtractor: new AiGatewayMessageExtractor(_numberOfShards))
                .ConfigureAwait(false);
            _region.Publish(_system, _proxy);

            _logger.LogInformation(
                "Cluster Sharding proxy initialized for entity type {EntityType} towards role {Role}.",
                AiGatewayEntityId.EntityTypeName,
                NodeRoleNames.Gateway);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Shared cluster-<em>Up</em> gate for the gateway workloads (US-F0-06-T04d contract, reused by
/// US-F1-05-T07): Cluster Sharding must only be touched once the node is a full cluster member.
/// </summary>
internal static class AkkaClusterUpGate
{
    public static async Task WaitAsync(
        ActorSystem system,
        string role,
        TimeSpan timeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var cluster = Cluster.Get(system);
        var memberUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.RegisterOnMemberUp(() => memberUp.TrySetResult());

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linkedCts =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeoutCts.Token);

        var waitForTimeoutOrCancel = Task.Delay(Timeout.Infinite, linkedCts.Token);
        var completed = await Task.WhenAny(memberUp.Task, waitForTimeoutOrCancel)
            .ConfigureAwait(false);
        if (completed == memberUp.Task)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var lastStatus = cluster.SelfMember.Status;
        logger.LogError(
            "Cluster Sharding for role {Role} was not initialized: the ActorSystem did not reach "
            + "cluster Up within {ClusterUpTimeout} (last self-member status: {SelfStatus}).",
            role,
            timeout,
            lastStatus);

        throw new ClusterStartupTimeoutException(role, timeout, lastStatus);
    }
}
