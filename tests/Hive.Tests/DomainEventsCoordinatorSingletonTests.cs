using Akka.Actor;
using Akka.Cluster;
using Akka.Configuration;
using Hive.Actors;
using Hive.Actors.Events;
using Hive.Actors.Sharding;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Organization.Registry;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class DomainEventsCoordinatorSingletonTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(45);

    [Theory]
    [InlineData(NodeRoleNames.Agents, true)]
    [InlineData(NodeRoleNames.Api, false)]
    public async Task Bootstrap_activates_only_agents_and_repeated_concurrent_starts_preserve_actor_refs(string role, bool active)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = role,
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = GetFreeTcpPort().ToString(System.Globalization.CultureInfo.InvariantCulture),
        });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        using var host = builder.Build();
        try
        {
            await host.StartAsync();
            var workload = host.Services.GetRequiredService<DomainEventsCoordinatorSingletonWorkload>();
            var started = host.Services.GetServices<IHostedService>().OfType<RoleWorkloadHostedService>().Single().StartedWorkloads;
            Assert.Equal(active, started.Contains(workload));
            Assert.Equal(active, workload.Manager is not null);
            Assert.Equal(active, workload.Proxy is not null);
            var manager = workload.Manager;
            var proxy = workload.Proxy;
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => workload.StartAsync(CancellationToken.None)));
            Assert.Same(manager, workload.Manager);
            Assert.Same(proxy, workload.Proxy);
            if (active)
            {
                Assert.Equal(DomainEventsCoordinatorIdentity.SingletonManagerName, manager!.Path.Name);
                Assert.Equal(DomainEventsCoordinatorIdentity.ProxyName, proxy!.Path.Name);
                Assert.Equal(MemberStatus.Up, Cluster.Get(host.Services.GetRequiredService<ActorSystem>()).SelfMember.Status);
                Assert.NotNull(await WhereIsAsync(proxy));
            }
        }
        finally { await host.StopAsync(); }
    }

    [Fact]
    public async Task Up_gate_times_out_or_cancels_without_actors_and_can_retry_after_join()
    {
        using var system = CreateSystem("domain-events-gate");
        var workload = Workload(system, TimeSpan.FromMilliseconds(250));
        try
        {
            var error = await Assert.ThrowsAsync<ClusterStartupTimeoutException>(() => workload.StartAsync(CancellationToken.None));
            Assert.Equal(NodeRoleNames.Agents, error.Role);
            Assert.Equal(workload.ClusterUpTimeout, error.Timeout);
            Assert.NotEqual(MemberStatus.Up, error.LastStatus);
            Assert.Null(workload.Manager);
            Assert.Null(workload.Proxy);
            using var cancellation = new CancellationTokenSource();
            var starting = workload.StartAsync(cancellation.Token);
            Assert.False(starting.IsCompleted);
            cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting);
            Assert.Null(workload.Manager);
            Assert.Null(workload.Proxy);
            var cluster = Cluster.Get(system);
            cluster.Join(cluster.SelfAddress);
            await WaitForAsync(() => cluster.SelfMember.Status == MemberStatus.Up);
            await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => workload.StartAsync(CancellationToken.None)));
            Assert.NotNull(await WhereIsAsync(workload.Proxy!));
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Local_proxy_switches_to_cluster_discovery_when_its_initial_target_terminates()
    {
        using var system = CreateSystem("domain-events-proxy-recovery");
        try
        {
            var cluster = Cluster.Get(system);
            cluster.Join(cluster.SelfAddress);
            var workload = Workload(system, Timeout);
            await workload.StartAsync(CancellationToken.None);
            var singleton = await WhereIsAsync(workload.Proxy!);
            var initial = system.ActorOf(DomainEventsCoordinator.Props());
            var proxy = system.ActorOf(DomainEventsCoordinatorProxy.Props(initial));
            Assert.Equal(initial, await WhereIsAsync(proxy));
            system.Stop(initial);
            await WaitForAsync(async () => (await WhereIsAsync(proxy)).Equals(singleton));
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Real_cluster_has_one_instance_and_hands_over_then_reconciles_without_duplicates()
    {
        var systems = new List<ActorSystem>();
        var workloads = new List<DomainEventsCoordinatorSingletonWorkload>();
        var systemName = $"domain-events-{Guid.NewGuid():N}";
        try
        {
            for (var i = 0; i < 3; i++)
            {
                var system = CreateSystem(systemName);
                systems.Add(system);
                Cluster.Get(system).Join(Cluster.Get(systems[0]).SelfAddress);
                await WaitForAsync(() => Cluster.Get(system).SelfMember.Status == MemberStatus.Up);
                var workload = Workload(system, Timeout);
                workloads.Add(workload);
                await workload.StartAsync(CancellationToken.None);
            }
            await WaitForAsync(() => systems.All(system => Cluster.Get(system).State.Members.Count(member => member.Status == MemberStatus.Up) == 3));
            var fromSecond = await WhereIsAsync(workloads[1].Proxy!);
            var fromThird = await WhereIsAsync(workloads[2].Proxy!);
            Assert.Equal(fromSecond, fromThird);
            Assert.Equal(Cluster.Get(systems[0]).SelfAddress, fromThird.Path.Address);
            await AssertNoLocalSingletonAsync(systems[1]);
            await AssertNoLocalSingletonAsync(systems[2]);
            var snapshot = await DomainEventsCoordinatorTests.ImportAsync(new InMemoryOrganizationRegistry());
            Assert.True((await DomainEventsCoordinatorTests.ReconcileAsync(workloads[0].Proxy!, snapshot)).IsChanged);

            var leaving = Cluster.Get(systems[0]);
            leaving.Leave(leaving.SelfAddress);
            await WaitForAsync(() => systems.Skip(1).All(system => Cluster.Get(system).State.Members.All(member => member.Address != leaving.SelfAddress)));
            await systems[0].Terminate().WaitAsync(Timeout);
            IActorRef? successor = null;
            await WaitForAsync(async () =>
            {
                successor = await WhereIsAsync(workloads[2].Proxy!);
                return successor.Path.Address == Cluster.Get(systems[1]).SelfAddress;
            });
            Assert.NotEqual(fromThird, successor);
            await AssertNoLocalSingletonAsync(systems[2]);
            var fresh = await workloads[1].Proxy!.Ask<DomainEventsCoordinatorState>(GetDomainEventsCoordinatorState.Instance, Timeout);
            Assert.Empty(fresh.Organizations);
            Assert.True((await DomainEventsCoordinatorTests.ReconcileAsync(workloads[1].Proxy!, snapshot)).IsChanged);
            var duplicate = await DomainEventsCoordinatorTests.ReconcileAsync(workloads[1].Proxy!, snapshot);
            Assert.True(duplicate.IsAccepted);
            Assert.False(duplicate.IsChanged);
            var rebuilt = await workloads[1].Proxy!.Ask<DomainEventsCoordinatorState>(GetDomainEventsCoordinatorState.Instance, Timeout);
            Assert.Single(rebuilt.Organizations);
        }
        finally
        {
            foreach (var system in systems)
                await system.Terminate().WaitAsync(Timeout);
        }
    }

    private static Task AssertNoLocalSingletonAsync(ActorSystem system) =>
        Assert.ThrowsAsync<ActorNotFoundException>(() => system.ActorSelection(
            $"{DomainEventsCoordinatorIdentity.SingletonManagerPath}/{DomainEventsCoordinatorIdentity.SingletonName}")
            .ResolveOne(TimeSpan.FromMilliseconds(500)));

    private static ActorSystem CreateSystem(string name) => ActorSystem.Create(name,
        ConfigurationFactory.ParseString("""
            akka.actor.provider = cluster
            akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
            akka.remote.dot-netty.tcp.port = 0
            akka.cluster.roles = ["agents"]
            akka.loglevel = WARNING
            """));

    private static int GetFreeTcpPort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static DomainEventsCoordinatorSingletonWorkload Workload(ActorSystem system, TimeSpan timeout) =>
        new(system, Options.Create(new HiveOptions { Agents = new AgentsNodeOptions { ClusterUpTimeout = timeout } }),
            NullLogger<DomainEventsCoordinatorSingletonWorkload>.Instance);

    private static async Task<IActorRef> WhereIsAsync(IActorRef proxy)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { return await proxy.Ask<IActorRef>(WhereIsDomainEventsCoordinator.Instance, TimeSpan.FromSeconds(3)); }
            catch (AskTimeoutException) { }
        }
        throw new TimeoutException("The singleton proxy did not resolve its coordinator.");
    }

    private static Task WaitForAsync(Func<bool> condition) => WaitForAsync(() => Task.FromResult(condition()));

    private static async Task WaitForAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException("Cluster condition was not met.");
    }
}
