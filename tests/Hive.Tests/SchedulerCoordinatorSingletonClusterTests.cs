using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Tools.Singleton;
using Akka.Configuration;
using Hive.Actors.Scheduling;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

/// <summary>
/// Validates the logical singleton behaviour of the <see cref="SchedulerCoordinator"/> in a real
/// multi-node cluster (US-F0-09-T03c): at startup exactly one active coordinator exists and is
/// reachable through the proxy from every node, and on restart of the node hosting it the Cluster
/// Singleton performs a single handover to another agents node — never two active coordinators
/// materializing the same schedules.
/// </summary>
[Collection(nameof(AkkaClusterCollection))]
public sealed class SchedulerCoordinatorSingletonClusterTests
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);
    private static readonly DateTimeOffset ImportAt = new(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Startup_materializes_exactly_one_active_coordinator_reachable_from_every_node()
    {
        await using var cluster = await SingletonCluster.StartAsync(nodeCount: 2);

        // The oldest node (the seed, joined first) hosts the single active singleton. Its proxy
        // resolves the active instance locally; every other node's proxy resolves the very same
        // instance across the wire.
        var fromOldest = await cluster.WhereIsAsync(nodeIndex: 0);
        var fromOther = await cluster.WhereIsAsync(nodeIndex: 1);

        Assert.NotNull(fromOldest);
        Assert.NotNull(fromOther);
        Assert.Equal(
            fromOldest!.Path.ToStringWithoutAddress(),
            fromOther!.Path.ToStringWithoutAddress());

        // The non-hosting node resolves the active instance across the wire, and its address is the
        // oldest node's — proof there is a single active coordinator, hosted on the oldest node.
        Assert.Equal(cluster.PortOf(0), fromOther.Path.Address.Port);

        // The single active instance actually processes work sent through the (local) proxy.
        var snapshot = await ImportedSnapshotAsync();
        var result = await cluster.ReconcileAsync(nodeIndex: 0, snapshot);
        Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            new[] { "acme-delivery/delivery-lead/daily-report" },
            result.Materializations.Select(materialization => materialization.Key.Value));
    }

    [Fact]
    public async Task Restart_of_the_hosting_node_hands_over_to_a_single_surviving_coordinator()
    {
        await using var cluster = await SingletonCluster.StartAsync(nodeCount: 3);

        // Before restart: a surviving observer node resolves the active instance on the oldest node.
        var beforeFromObserver = await cluster.WhereIsAsync(nodeIndex: 2);
        Assert.NotNull(beforeFromObserver);
        Assert.Equal(cluster.PortOf(0), beforeFromObserver!.Path.Address.Port);

        // Restart/failover: the node hosting the active singleton leaves the cluster gracefully.
        await cluster.LeaveNodeAsync(nodeIndex: 0);

        // After handover: the same observer node now resolves the active instance on the next oldest
        // node (index 1). There is still exactly one active coordinator — it moved, it was not
        // duplicated.
        var afterFromObserver = await cluster.WaitForWhereIsOnPortAsync(
            nodeIndex: 2,
            expectedPort: cluster.PortOf(1));
        Assert.NotNull(afterFromObserver);
        Assert.Equal(cluster.PortOf(1), afterFromObserver!.Path.Address.Port);

        // The surviving active instance reconciles the same snapshot without duplicate materializations.
        var snapshot = await ImportedSnapshotAsync();
        var result = await cluster.ReconcileAsync(nodeIndex: 1, snapshot);
        Assert.True(result.IsAccepted, string.Join(Environment.NewLine, result.Errors));
        Assert.Equal(
            new[] { "acme-delivery/delivery-lead/daily-report" },
            result.Materializations.Select(materialization => materialization.Key.Value));
    }

    private sealed class SingletonCluster : IAsyncDisposable
    {
        private readonly List<SingletonNode> _nodes;

        private SingletonCluster(List<SingletonNode> nodes)
        {
            _nodes = nodes;
        }

        public static async Task<SingletonCluster> StartAsync(int nodeCount)
        {
            var systemName = $"hive-scheduler-singleton-{Guid.NewGuid():N}";
            var nodes = new List<SingletonNode>();
            var cluster = new SingletonCluster(nodes);
            try
            {
                for (var index = 0; index < nodeCount; index++)
                {
                    var node = SingletonNode.Create(systemName, GetFreeTcpPort());
                    nodes.Add(node);

                    // Join sequentially and wait for each node to reach Up before joining the next,
                    // so member age (and therefore the singleton host) is deterministic: node 0 is
                    // always the oldest, node 1 the next oldest, and so on.
                    var seed = Address.Parse(
                        $"akka.tcp://{systemName}@127.0.0.1:{nodes[0].Port}");
                    Cluster.Get(node.System).JoinSeedNodes([seed]);
                    await WaitForAsync(
                        () => Cluster.Get(node.System).SelfMember.Status == MemberStatus.Up,
                        Timeout);

                    node.Start();
                }

                await WaitForAsync(
                    () => nodes.All(candidate =>
                        Cluster.Get(candidate.System).State.Members
                            .Count(member => member.Status == MemberStatus.Up) == nodes.Count),
                    Timeout);

                return cluster;
            }
            catch
            {
                await cluster.DisposeAsync();
                throw;
            }
        }

        public int PortOf(int nodeIndex) => _nodes[nodeIndex].Port;

        public async Task<IActorRef?> WhereIsAsync(int nodeIndex)
        {
            var proxy = _nodes[nodeIndex].Proxy;
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    return await proxy.Ask<IActorRef>(WhereIsSchedulerCoordinator.Instance, AskTimeout);
                }
                catch (AskTimeoutException)
                {
                    // Proxy has not located the active singleton yet; retry until the deadline.
                }

                await Task.Delay(200);
            }

            return null;
        }

        public async Task<IActorRef?> WaitForWhereIsOnPortAsync(int nodeIndex, int? expectedPort)
        {
            var deadline = DateTime.UtcNow + Timeout;
            while (DateTime.UtcNow < deadline)
            {
                var reference = await WhereIsAsync(nodeIndex);
                if (reference is not null && reference.Path.Address.Port == expectedPort)
                {
                    return reference;
                }

                await Task.Delay(250);
            }

            return null;
        }

        public Task<SchedulerReconciliationResult> ReconcileAsync(
            int nodeIndex,
            OrganizationRegistrySnapshot snapshot) =>
            _nodes[nodeIndex].Proxy.Ask<SchedulerReconciliationResult>(
                new ReconcileSchedulerSchedules(snapshot),
                Timeout);

        public async Task LeaveNodeAsync(int nodeIndex)
        {
            var leaving = _nodes[nodeIndex];
            var cluster = Cluster.Get(leaving.System);
            var address = cluster.SelfAddress;
            cluster.Leave(address);

            var survivors = _nodes.Where((_, index) => index != nodeIndex).ToArray();
            await WaitForAsync(
                () => survivors.All(candidate => Cluster.Get(candidate.System)
                    .State.Members.All(member => member.Address != address)),
                Timeout);

            await leaving.System.Terminate().WaitAsync(Timeout);
            _nodes[nodeIndex] = leaving with { Active = false };
        }

        public async ValueTask DisposeAsync()
        {
            foreach (var node in _nodes.Where(node => node.Active))
            {
                await node.System.Terminate();
            }
        }
    }

    private sealed record SingletonNode(ActorSystem System, int Port, IActorRef Manager, IActorRef Proxy)
    {
        public bool Active { get; init; } = true;

        public static SingletonNode Create(string systemName, int port)
        {
            var system = ActorSystem.Create(
                systemName,
                ConfigurationFactory.ParseString($$"""
                    akka.actor.provider = cluster
                    akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
                    akka.remote.dot-netty.tcp.port = {{port}}
                    akka.cluster.roles = ["{{NodeRoleNames.Agents}}"]
                    akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                    akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                    """));

            var managerSettings = ClusterSingletonManagerSettings.Create(system)
                .WithRole(NodeRoleNames.Agents)
                .WithSingletonName(SchedulerCoordinatorIdentity.SingletonName);
            var manager = system.ActorOf(
                ClusterSingletonManager.Props(
                    SchedulerCoordinator.Props(
                        NoopSchedulerQuartzAdapter.Instance,
                        TimeProvider.System),
                    PoisonPill.Instance,
                    managerSettings),
                SchedulerCoordinatorIdentity.SingletonManagerName);

            var proxySettings = ClusterSingletonProxySettings.Create(system)
                .WithRole(NodeRoleNames.Agents)
                .WithSingletonName(SchedulerCoordinatorIdentity.SingletonName);
            var proxy = system.ActorOf(
                ClusterSingletonProxy.Props(
                    SchedulerCoordinatorIdentity.SingletonManagerPath,
                    proxySettings),
                SchedulerCoordinatorIdentity.ProxyName);

            // The manager/proxy are created eagerly on construction; expose the node once joined.
            return new SingletonNode(system, port, manager, proxy);
        }

        // Manager/proxy are already started in Create(); kept for symmetry with the join loop.
        public void Start()
        {
        }
    }

    private static async Task<OrganizationRegistrySnapshot> ImportedSnapshotAsync()
    {
        var configuration = WithDeliveryLeadDailyReport(ExampleConfiguration());
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
            .ImportAsync(configuration);

        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        return imported.Snapshot!;
    }

    private static OrganizationConfiguration WithDeliveryLeadDailyReport(
        OrganizationConfiguration configuration) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        new OccupantConfiguration(
                            position.Occupant.Type,
                            position.Occupant.IdentityPromptRef,
                            position.Occupant.Ai,
                            new WorkingHoursConfiguration("09:00", "18:00"),
                            position.Occupant.Authority,
                            [new ScheduleEntryConfiguration(
                                "daily-report",
                                "0 55 17 ? * MON-FRI",
                                "Run daily report")],
                            position.Occupant.Subscriptions,
                            position.Occupant.Tools),
                        position.ReportsTo,
                        position.Name,
                        "Europe/Lisbon")
                    : position)
                .ToArray(),
            configuration.Prompts);

    private static OrganizationConfiguration ExampleConfiguration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(RepositoryRoot, "config", "organizations", "acme-delivery", "organization.yaml"));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
