using Akka.Actor;
using Akka.Cluster;
using Hive.Actors;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class HiveActorSystemTests
{
    [Fact]
    public async Task Single_node_host_forms_a_real_cluster_and_announces_declared_roles()
    {
        var port = GetFreeTcpPort();
        using var host = BuildHost(port, NodeRoleNames.Agents, NodeRoleNames.Api);

        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var cluster = Cluster.Get(system);

            await WaitForAsync(
                () => cluster.SelfMember.Status == MemberStatus.Up,
                TimeSpan.FromSeconds(20));

            Assert.Equal(MemberStatus.Up, cluster.SelfMember.Status);
            Assert.Contains(NodeRoleNames.Agents, cluster.SelfMember.Roles);
            Assert.Contains(NodeRoleNames.Api, cluster.SelfMember.Roles);
            Assert.DoesNotContain(NodeRoleNames.Gateway, cluster.SelfMember.Roles);
        }
        finally
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(int port, params string[] roles)
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

[CollectionDefinition(nameof(AkkaClusterCollection), DisableParallelization = true)]
public sealed class AkkaClusterCollection
{
}
