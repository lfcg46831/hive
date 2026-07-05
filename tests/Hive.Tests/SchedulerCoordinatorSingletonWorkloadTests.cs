using Akka.Actor;
using Akka.Cluster;
using Hive.Actors;
using Hive.Actors.Scheduling;
using Hive.Actors.Sharding;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class SchedulerCoordinatorSingletonWorkloadTests
{
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Agents_node_materializes_manager_and_proxy_under_stable_names()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<SchedulerCoordinatorSingletonWorkload>();
            await WaitForAsync(() => workload.Proxy is not null, TimeSpan.FromSeconds(20));

            Assert.NotNull(workload.Manager);
            Assert.NotNull(workload.Proxy);
            Assert.Equal(
                SchedulerCoordinatorIdentity.SingletonManagerName,
                workload.Manager!.Path.Name);
            Assert.Equal(
                SchedulerCoordinatorIdentity.ProxyName,
                workload.Proxy!.Path.Name);

            // The single active instance is reachable through the proxy on this node.
            var coordinator = await AskWhereIsAsync(workload.Proxy, TimeSpan.FromSeconds(20));
            Assert.NotNull(coordinator);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Starting_the_workload_again_is_idempotent()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<SchedulerCoordinatorSingletonWorkload>();
            await WaitForAsync(() => workload.Proxy is not null, TimeSpan.FromSeconds(20));
            var managerAfterFirstStart = workload.Manager;
            var proxyAfterFirstStart = workload.Proxy;

            await workload.StartAsync(CancellationToken.None);

            Assert.Same(managerAfterFirstStart, workload.Manager);
            Assert.Same(proxyAfterFirstStart, workload.Proxy);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Non_agents_node_does_not_materialize_the_singleton()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Api]);

        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            await WaitForAsync(
                () => Cluster.Get(system).SelfMember.Status == MemberStatus.Up,
                TimeSpan.FromSeconds(20));

            var workload = host.Services.GetRequiredService<SchedulerCoordinatorSingletonWorkload>();
            Assert.Null(workload.Manager);
            Assert.Null(workload.Proxy);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Agents_node_fails_observably_when_the_cluster_does_not_reach_up_in_time()
    {
        // Point the single seed node at a free (unreachable) port so this node can never self-seed
        // nor join, and never reaches cluster Up. With a small gate window the workload must fail
        // the arranque observably instead of materializing the singleton on a node that has not
        // joined the cluster.
        var unreachableSeed =
            $"akka.tcp://{HiveActorSystemBootstrapExtensions.ActorSystemName}@127.0.0.1:{GetFreeTcpPort()}";
        using var host = BuildHost(
            GetFreeTcpPort(),
            roles: [NodeRoleNames.Agents],
            clusterUpTimeout: TimeSpan.FromSeconds(2),
            seedNode: unreachableSeed);

        var workload = host.Services.GetRequiredService<SchedulerCoordinatorSingletonWorkload>();

        var exception = await Assert.ThrowsAsync<ClusterStartupTimeoutException>(
            () => workload.StartAsync(CancellationToken.None));

        Assert.Equal(NodeRoleNames.Agents, exception.Role);
        Assert.Equal(TimeSpan.FromSeconds(2), exception.Timeout);
        Assert.NotEqual(MemberStatus.Up, exception.LastStatus);
        Assert.Null(workload.Manager);
        Assert.Null(workload.Proxy);

        await host.StopAsync();
    }

    private static async Task<IActorRef?> AskWhereIsAsync(IActorRef proxy, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                return await proxy.Ask<IActorRef>(WhereIsSchedulerCoordinator.Instance, AskTimeout);
            }
            catch (AskTimeoutException)
            {
                // The proxy has not located the active singleton yet; retry until the deadline.
            }

            await Task.Delay(200);
        }

        return null;
    }

    private static IHost BuildHost(
        int port,
        string[] roles,
        TimeSpan? clusterUpTimeout = null,
        string? seedNode = null)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });

        var settings = new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
        for (var index = 0; index < roles.Length; index++)
        {
            settings[$"Hive:Node:Roles:{index}"] = roles[index];
        }

        if (clusterUpTimeout is { } upTimeout)
        {
            settings["Hive:Agents:ClusterUpTimeout"] =
                upTimeout.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        }

        if (seedNode is not null)
        {
            settings["Hive:Cluster:SeedNodes:0"] = seedNode;
        }

        builder.Configuration.AddInMemoryCollection(settings);
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        return builder.Build();
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
}
