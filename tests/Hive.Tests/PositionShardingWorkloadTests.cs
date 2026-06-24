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

    private static IHost BuildHost(int port, string[] roles, int? numberOfShards = null)
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
