using Akka.Cluster.Hosting;
using Akka.Hosting;
using Akka.Remote.Hosting;
using Hive.Actors.Serialization;
using Hive.Actors.Sharding;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
                .ConfigureLoggers(logging =>
                {
                    // Route Akka's own logs through the host's ILoggerFactory so the process
                    // emits one uniform, structured stream (US-F0-01-T07) instead of Akka's
                    // default unstructured stdout logger alongside the common JSON console.
                    logging.ClearLoggers();
                    logging.AddLoggerFactory();
                })
                // Bind the organizational message protocol to the versionable System.Text.Json
                // serializer (US-F0-03-T08, ADR-007). Binding the OrgMessage base type covers every
                // canonical subtype and overrides Akka's default serializer for remote/cluster
                // delivery; the same format is reused for persisted events/snapshots.
                .WithCustomSerializer(
                    "hive-org-message",
                    new[] { typeof(OrgMessage) },
                    system => new OrgMessageJsonSerializer(system))
                .WithRemoting(cluster.Hostname, cluster.Port)
                .WithClustering(new ClusterOptions
                {
                    Roles = roles,
                    SeedNodes = seedNodes,
                });
        });

        // Cluster Sharding for the PositionActor is a role-conditional workload (US-F0-06-T04b):
        // the host starts it only on nodes that declare the agents role, through the existing
        // IRoleWorkload seam. The entity Props seam defaults to a placeholder until the real
        // persistent PositionActor lands (US-F0-06-T06b/T09); TryAdd lets those stories override
        // it without touching this wiring.
        builder.Services.TryAddSingleton<IPositionEntityProps, PlaceholderPositionEntityProps>();
        builder.Services.AddSingleton<PositionShardingWorkload>();
        builder.Services.AddSingleton<IRoleWorkload>(
            sp => sp.GetRequiredService<PositionShardingWorkload>());

        return builder;
    }
}
