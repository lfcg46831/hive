using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Tools.Singleton;
using Hive.Actors.Sharding;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Events;

/// <summary>Hosts one coordinator across the agents role, after cluster admission.</summary>
public sealed class DomainEventsCoordinatorSingletonWorkload : IRoleWorkload
{
    public static readonly TimeSpan DefaultClusterUpTimeout = TimeSpan.FromSeconds(30);
    private readonly ActorSystem _system;
    private readonly ILogger<DomainEventsCoordinatorSingletonWorkload> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);

    public DomainEventsCoordinatorSingletonWorkload(ActorSystem system, IOptions<HiveOptions> options,
        ILogger<DomainEventsCoordinatorSingletonWorkload> logger)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        ArgumentNullException.ThrowIfNull(options);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        ClusterUpTimeout = options.Value.Agents?.ClusterUpTimeout ?? DefaultClusterUpTimeout;
    }

    public string Role => NodeRoleNames.Agents;
    public TimeSpan ClusterUpTimeout { get; }
    public IActorRef? Manager { get; private set; }
    public IActorRef? Proxy { get; private set; }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Proxy is not null)
                return;

            var cluster = Cluster.Get(_system);
            if (!cluster.SelfMember.Roles.Contains(Role))
                return;

            var memberUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            cluster.RegisterOnMemberUp(() => memberUp.TrySetResult());
            try
            {
                await memberUp.Task.WaitAsync(ClusterUpTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogError("Domain events coordinator did not reach cluster Up within {ClusterUpTimeout} "
                    + "(last self-member status: {SelfStatus}).", ClusterUpTimeout, cluster.SelfMember.Status);
                throw new ClusterStartupTimeoutException(Role, ClusterUpTimeout, cluster.SelfMember.Status);
            }

            cancellationToken.ThrowIfCancellationRequested();
            Manager ??= _system.ActorOf(ClusterSingletonManager.Props(
                    DomainEventsCoordinator.Props(), PoisonPill.Instance,
                    ClusterSingletonManagerSettings.Create(_system).WithRole(Role)
                        .WithSingletonName(DomainEventsCoordinatorIdentity.SingletonName)),
                DomainEventsCoordinatorIdentity.SingletonManagerName);
            var local = await TryResolveSoleAgentsSingletonAsync(cluster, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Proxy = _system.ActorOf(local is null
                    ? DomainEventsCoordinatorProxy.ClusterProps(_system)
                    : DomainEventsCoordinatorProxy.Props(local),
                DomainEventsCoordinatorIdentity.ProxyName);

            _logger.LogInformation("Domain events coordinator singleton materialized on role {Role} "
                + "(manager={Manager}, singleton={Singleton}, proxy={Proxy}).", Role,
                DomainEventsCoordinatorIdentity.SingletonManagerName,
                DomainEventsCoordinatorIdentity.SingletonName,
                DomainEventsCoordinatorIdentity.ProxyName);
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task<IActorRef?> TryResolveSoleAgentsSingletonAsync(Cluster cluster, CancellationToken cancellationToken)
    {
        if (cluster.State.Members.Any(member => member.Address != cluster.SelfAddress
            && member.Status == MemberStatus.Up && member.Roles.Contains(Role)))
            return null;

        // As in the scheduler, avoid the Akka proxy's initial warning when the only agents
        // member can resolve its own singleton. The forwarding proxy watches that reference
        // and switches to cluster discovery if it terminates, so it cannot retain a stale target.
        var selection = _system.ActorSelection(
            $"{DomainEventsCoordinatorIdentity.SingletonManagerPath}/{DomainEventsCoordinatorIdentity.SingletonName}");
        var deadline = DateTimeOffset.UtcNow + ClusterUpTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await selection.ResolveOne(TimeSpan.FromMilliseconds(100))
                    .WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ActorNotFoundException) { }
            catch (AskTimeoutException) { }
            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken).ConfigureAwait(false);
        }
        return null;
    }

    // Akka coordinated shutdown owns manager termination and cluster handover.
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

internal sealed class DomainEventsCoordinatorProxy : ReceiveActor
{
    private IActorRef _target;

    public DomainEventsCoordinatorProxy(IActorRef target)
    {
        _target = target;
        Context.Watch(target);
        Receive<Terminated>(terminated =>
        {
            if (terminated.ActorRef.Equals(_target))
                _target = Context.ActorOf(ClusterProps(Context.System), "cluster-proxy");
        });
        ReceiveAny(message => _target.Forward(message));
    }

    public static Props Props(IActorRef target) => Akka.Actor.Props.Create(() => new DomainEventsCoordinatorProxy(target));

    internal static Props ClusterProps(ActorSystem system) => ClusterSingletonProxy.Props(
        DomainEventsCoordinatorIdentity.SingletonManagerPath,
        ClusterSingletonProxySettings.Create(system).WithRole(NodeRoleNames.Agents)
            .WithSingletonName(DomainEventsCoordinatorIdentity.SingletonName));
}
