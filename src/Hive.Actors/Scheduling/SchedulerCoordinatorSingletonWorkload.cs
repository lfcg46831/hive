using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Tools.Singleton;
using Hive.Actors.Sharding;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Scheduling;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Scheduling;

/// <summary>
/// Materializes the <see cref="SchedulerCoordinator"/> as an Akka Cluster Singleton on the
/// <see cref="NodeRoleNames.Agents"/> role (US-F0-09-T03c). The coordinator must exist exactly once
/// in the cluster so two nodes never materialize and dispatch the same schedules in parallel: a
/// <see cref="ClusterSingletonManager"/> hosts the single active instance under the stable logical
/// name <see cref="SchedulerCoordinatorIdentity.LogicalName"/>, and a
/// <see cref="ClusterSingletonProxy"/> lets any node reach that instance without knowing which node
/// currently hosts it.
/// </summary>
/// <remarks>
/// <para>
/// Like the position sharding workload, this gates its start on the node reaching cluster
/// <em>Up</em> within a configured window and fails the arranque observably otherwise (reusing the
/// <see cref="ClusterStartupTimeoutException"/> of US-F0-06-T04d). The singleton lives on the
/// <see cref="NodeRoleNames.Agents"/> role — the same nodes that host the sharded positions the
/// <c>Pulse</c> will be delivered to — so no new node role is introduced.
/// </para>
/// <para>
/// On restart/failover of the node hosting the active instance, Akka's Cluster Singleton performs a
/// single deterministic handover: the old instance is stopped (via the <see cref="PoisonPill"/>
/// termination message) before the new one starts on another agents node, so two coordinators never
/// coexist. The coordinator keeps no persisted state; after a handover the new instance rebuilds its
/// materialization by reconciling the registry snapshot again.
/// </para>
/// </remarks>
public sealed class SchedulerCoordinatorSingletonWorkload : IRoleWorkload
{
    /// <summary>
    /// Placement default for <see cref="ClusterUpTimeout"/> when the host leaves it unset: the
    /// window the workload waits for the node to reach cluster <em>Up</em> before failing the
    /// arranque observably (mirrors the position sharding workload).
    /// </summary>
    public static readonly TimeSpan DefaultClusterUpTimeout = TimeSpan.FromSeconds(30);

    private readonly ActorSystem _system;
    private readonly Props _coordinatorProps;
    private readonly TimeSpan _clusterUpTimeout;
    private readonly ILogger<SchedulerCoordinatorSingletonWorkload> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    private IActorRef? _manager;
    private IActorRef? _proxy;

    public SchedulerCoordinatorSingletonWorkload(
        ActorSystem system,
        IOptions<HiveOptions> options,
        ILogger<SchedulerCoordinatorSingletonWorkload> logger)
        : this(system, SchedulerCoordinator.Props(), options, logger)
    {
    }

    public SchedulerCoordinatorSingletonWorkload(
        ActorSystem system,
        IOptions<HiveOptions> options,
        ILogger<SchedulerCoordinatorSingletonWorkload> logger,
        ISchedulerPulseDeliveryStore deliveryStore)
        : this(
            system,
            options,
            logger,
            deliveryStore,
            AkkaClusterShardingSchedulerPulseDispatcher.Instance)
    {
    }

    public SchedulerCoordinatorSingletonWorkload(
        ActorSystem system,
        IOptions<HiveOptions> options,
        ILogger<SchedulerCoordinatorSingletonWorkload> logger,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher)
        : this(
            system,
            options,
            logger,
            deliveryStore,
            pulseDispatcher,
            AllowingSchedulerProactiveBudgetPolicy.Instance)
    {
    }

    public SchedulerCoordinatorSingletonWorkload(
        ActorSystem system,
        IOptions<HiveOptions> options,
        ILogger<SchedulerCoordinatorSingletonWorkload> logger,
        ISchedulerPulseDeliveryStore deliveryStore,
        ISchedulerPulseDispatcher pulseDispatcher,
        ISchedulerProactiveBudgetPolicy proactiveBudgetPolicy)
        : this(
            system,
            SchedulerCoordinator.Props(
                new AkkaQuartzSchedulerAdapter(),
                TimeProvider.System,
                deliveryStore,
                pulseDispatcher,
                proactiveBudgetPolicy),
            options,
            logger)
    {
    }

    internal SchedulerCoordinatorSingletonWorkload(
        ActorSystem system,
        Props coordinatorProps,
        IOptions<HiveOptions> options,
        ILogger<SchedulerCoordinatorSingletonWorkload> logger)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _coordinatorProps = coordinatorProps ?? throw new ArgumentNullException(nameof(coordinatorProps));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        // The cluster-up gate window (US-F0-06-T04d): how long to wait for this node to reach
        // cluster Up before materializing the singleton. Falls back to the placement default; a
        // non-positive configured value is rejected up front by HiveOptionsValidator.
        _clusterUpTimeout = options.Value.Agents?.ClusterUpTimeout ?? DefaultClusterUpTimeout;
    }

    public string Role => NodeRoleNames.Agents;

    /// <summary>The started singleton manager, or <see langword="null"/> before the first start.</summary>
    public IActorRef? Manager => _manager;

    /// <summary>The started singleton proxy, or <see langword="null"/> before the first start.</summary>
    public IActorRef? Proxy => _proxy;

    /// <summary>The window the workload waits for cluster <em>Up</em> before failing (US-F0-06-T04d).</summary>
    public TimeSpan ClusterUpTimeout => _clusterUpTimeout;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_proxy is not null)
            {
                // Idempotent: already materialized on this node, return without re-registering.
                return;
            }

            // Ordering gate (US-F0-06-T04d): the singleton manager must only be created once this
            // node is a full cluster member, so wait for self-member Up before touching it. If the
            // node does not reach Up within the configured window, fail the arranque observably.
            await WaitForClusterUpAsync(cancellationToken).ConfigureAwait(false);

            var managerSettings = ClusterSingletonManagerSettings.Create(_system)
                .WithRole(NodeRoleNames.Agents)
                .WithSingletonName(SchedulerCoordinatorIdentity.SingletonName);

            _manager = _system.ActorOf(
                ClusterSingletonManager.Props(
                    _coordinatorProps,
                    PoisonPill.Instance,
                    managerSettings),
                SchedulerCoordinatorIdentity.SingletonManagerName);

            var proxySettings = ClusterSingletonProxySettings.Create(_system)
                .WithRole(NodeRoleNames.Agents)
                .WithSingletonName(SchedulerCoordinatorIdentity.SingletonName);

            _proxy = _system.ActorOf(
                ClusterSingletonProxy.Props(
                    SchedulerCoordinatorIdentity.SingletonManagerPath,
                    proxySettings),
                SchedulerCoordinatorIdentity.ProxyName);

            _logger.LogInformation(
                "Scheduler coordinator singleton materialized on role {Role} "
                + "(manager={Manager}, singleton={Singleton}, proxy={Proxy}).",
                NodeRoleNames.Agents,
                SchedulerCoordinatorIdentity.SingletonManagerName,
                SchedulerCoordinatorIdentity.SingletonName,
                SchedulerCoordinatorIdentity.ProxyName);
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

        cancellationToken.ThrowIfCancellationRequested();

        var lastStatus = cluster.SelfMember.Status;
        _logger.LogError(
            "Scheduler coordinator singleton for role {Role} was not materialized: the ActorSystem "
            + "did not reach cluster Up within {ClusterUpTimeout} (last self-member status: {SelfStatus}).",
            NodeRoleNames.Agents,
            _clusterUpTimeout,
            lastStatus);

        throw new ClusterStartupTimeoutException(NodeRoleNames.Agents, _clusterUpTimeout, lastStatus);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The manager/proxy lifecycle is bound to the ActorSystem and torn down by Akka's
        // coordinated shutdown when the host stops; there is nothing extra to do here.
        return Task.CompletedTask;
    }
}
