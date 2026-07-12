using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Hive.Actors;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class PositionShardingWorkloadTests
{
    [Fact]
    public async Task Agents_node_initializes_the_position_shard_region_under_the_stable_name()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            await WaitForAsync(() => workload.Region is not null, TimeSpan.FromSeconds(20));

            Assert.NotNull(workload.Region);

            var system = host.Services.GetRequiredService<ActorSystem>();
            var region = ClusterSharding.Get(system).ShardRegion(PositionEntityId.EntityTypeName);
            Assert.Equal(workload.Region, region);
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
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            await WaitForAsync(() => workload.Region is not null, TimeSpan.FromSeconds(20));
            var regionAfterFirstStart = workload.Region;

            await workload.StartAsync(CancellationToken.None);

            Assert.Same(regionAfterFirstStart, workload.Region);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Non_agents_node_does_not_initialize_the_position_shard_region()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Api]);

        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            await WaitForAsync(
                () => Cluster.Get(system).SelfMember.Status == MemberStatus.Up,
                TimeSpan.FromSeconds(20));

            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.Null(workload.Region);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Shard_count_defaults_to_the_placement_contract_when_unset()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.Equal(PositionMessageExtractor.DefaultNumberOfShards, workload.NumberOfShards);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Shard_count_is_pinned_from_configuration_when_set()
    {
        const int configuredShards = 8;
        using var host = BuildHost(
            GetFreeTcpPort(),
            roles: [NodeRoleNames.Agents],
            numberOfShards: configuredShards);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.Equal(configuredShards, workload.NumberOfShards);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Remember_entities_defaults_to_true_and_passivation_to_the_placement_default()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.True(workload.RememberEntities);
            Assert.Equal(
                PositionShardingWorkload.DefaultPassivateIdleAfter,
                workload.PassivateIdleAfter);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Remember_entities_and_passivation_threshold_are_pinned_from_configuration()
    {
        var idle = TimeSpan.FromSeconds(45);
        using var host = BuildHost(
            GetFreeTcpPort(),
            roles: [NodeRoleNames.Agents],
            rememberEntities: false,
            passivateIdleAfter: idle);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.False(workload.RememberEntities);
            Assert.Equal(idle, workload.PassivateIdleAfter);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Cluster_up_timeout_defaults_to_the_placement_default_when_unset()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.Equal(
                PositionShardingWorkload.DefaultClusterUpTimeout,
                workload.ClusterUpTimeout);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Cluster_up_timeout_is_pinned_from_configuration_when_set()
    {
        var configured = TimeSpan.FromSeconds(10);
        using var host = BuildHost(
            GetFreeTcpPort(),
            roles: [NodeRoleNames.Agents],
            clusterUpTimeout: configured);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            Assert.Equal(configured, workload.ClusterUpTimeout);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Region_is_initialized_only_after_the_node_reaches_cluster_up()
    {
        using var host = BuildHost(GetFreeTcpPort(), roles: [NodeRoleNames.Agents]);

        await host.StartAsync();
        try
        {
            var workload = host.Services.GetRequiredService<PositionShardingWorkload>();
            await WaitForAsync(() => workload.Region is not null, TimeSpan.FromSeconds(20));

            // The gate guarantees the node was Up before the region was initialized.
            var system = host.Services.GetRequiredService<ActorSystem>();
            Assert.Equal(MemberStatus.Up, Cluster.Get(system).SelfMember.Status);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    [Fact]
    public async Task Agents_node_fails_observably_when_the_cluster_does_not_reach_up_in_time()
    {
        // Reserve a distinct non-Akka endpoint for the seed so the node can neither self-seed nor
        // join. Keeping the listener bound prevents Windows from immediately recycling the same
        // ephemeral port for the node under test, which would make this test intermittently self-seed.
        using var nonClusterSeed = ReserveTcpPort();
        var unreachableSeed =
            $"akka.tcp://{HiveActorSystemBootstrapExtensions.ActorSystemName}@127.0.0.1:{((System.Net.IPEndPoint)nonClusterSeed.LocalEndpoint).Port}";
        using var host = BuildHost(
            GetFreeTcpPort(),
            roles: [NodeRoleNames.Agents],
            clusterUpTimeout: TimeSpan.FromSeconds(2),
            seedNode: unreachableSeed);

        // Resolving the workload creates the ActorSystem, which starts trying to join the seed.
        var workload = host.Services.GetRequiredService<PositionShardingWorkload>();

        var exception = await Assert.ThrowsAsync<ClusterStartupTimeoutException>(
            () => workload.StartAsync(CancellationToken.None));

        Assert.Equal(NodeRoleNames.Agents, exception.Role);
        Assert.Equal(TimeSpan.FromSeconds(2), exception.Timeout);
        Assert.NotEqual(MemberStatus.Up, exception.LastStatus);
        Assert.Null(workload.Region);

        await host.StopAsync();
    }

    private static IHost BuildHost(
        int port,
        string[] roles,
        int? numberOfShards = null,
        bool? rememberEntities = null,
        TimeSpan? passivateIdleAfter = null,
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

        if (numberOfShards is { } shards)
        {
            settings["Hive:Agents:NumberOfShards"] =
                shards.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (rememberEntities is { } remember)
        {
            settings["Hive:Agents:RememberEntities"] =
                remember.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        if (passivateIdleAfter is { } idle)
        {
            settings["Hive:Agents:PassivateIdleAfter"] =
                idle.ToString(null, System.Globalization.CultureInfo.InvariantCulture);
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

    private static System.Net.Sockets.TcpListener ReserveTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }
}
