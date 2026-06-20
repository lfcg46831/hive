using Akka.Cluster.Hosting;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Actors;

/// <summary>
/// Wires the minimal Akka.NET process for the node (US-F0-01-T06): a real Akka.Cluster actor
/// system whose cluster roles mirror the node's declared <c>Hive:Node:Roles</c>. By default it
/// self-seeds a single-node cluster so a host boots standalone; multi-node topologies override
/// hostname/port/seed nodes via configuration (US-F0-02). Cluster Sharding, Singletons and the
/// real position workloads are layered on top in later stories without changing this seam.
/// </summary>
public static class HiveActorSystemBootstrapExtensions
{
    public const string ActorSystemName = "hive";

    public static IHostApplicationBuilder AddHiveActorSystem(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Services.AddAkka(ActorSystemName, (akka, serviceProvider) =>
        {
            var cluster = serviceProvider.GetRequiredService<IOptions<HiveOptions>>().Value.Cluster;
            var roles = serviceProvider.GetRequiredService<ActiveNodeRoles>().Values.ToArray();

            string[] seedNodes = cluster.SeedNodes is { Length: > 0 }
                ? cluster.SeedNodes
                : new[] { $"akka.tcp://{ActorSystemName}@{cluster.Hostname}:{cluster.Port}" };

            akka
                .WithRemoting(cluster.Hostname, cluster.Port)
                .WithClustering(new ClusterOptions
                {
                    Roles = roles,
                    SeedNodes = seedNodes,
                });
        });

        return builder;
    }
}
