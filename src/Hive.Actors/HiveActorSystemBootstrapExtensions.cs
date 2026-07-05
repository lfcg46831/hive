using Akka.Cluster.Hosting;
using Akka.Hosting;
using Akka.Persistence.Sql.Hosting;
using Akka.Persistence.Hosting;
using Akka.Remote.Hosting;
using Hive.Actors.Positions;
using Hive.Actors.Scheduling;
using Hive.Actors.Serialization;
using Hive.Actors.Sharding;
using Hive.Domain.Messaging;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Persistence.PostgreSql;
using Microsoft.Extensions.Configuration;
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
            var configuration = serviceProvider.GetRequiredService<IConfiguration>();

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
                // Bind the sharded/persisted PositionActor protocol (US-F0-06-T05b): envelopes,
                // commands, events and snapshots use stable manifests over System.Text.Json, so
                // remote delivery and Akka.Persistence never fall back to CLR/.NET serialization.
                .WithCustomSerializer(
                    "hive-position-protocol",
                    PositionProtocolManifests.ProtocolTypes,
                    system => new PositionProtocolJsonSerializer(system))
                .WithRemoting(cluster.Hostname, cluster.Port)
                .WithClustering(new ClusterOptions
                {
                    Roles = roles,
                    SeedNodes = seedNodes,
                });

            // Akka.Persistence (Linq2Db, Akka.Persistence.Sql) PostgreSQL journal and snapshot store
            // for the PositionActor (US-F0-06-T05a, ADR-003 — Akka.Persistence.Sql replaces the
            // deprecated Akka.Persistence.PostgreSql). The plugins share the single
            // ConnectionStrings:PostgreSql value and live in their own dedicated schema, isolated from
            // the registry/audit/read model/budget/scheduler subsystems. The subsystem owns and
            // versions the dedicated schema via the migration that runs in the common bootstrap before
            // the agents workload starts the persistent entity; the journal/snapshot table DDL is
            // owned by the plugin and created by auto-initialization inside that schema, so the schema
            // exists first. When no connection string is configured the persistence plugins are not
            // wired and the node stays not-ready under the existing readiness contract, mirroring how
            // the registry import is skipped.
            var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
            if (!string.IsNullOrWhiteSpace(connectionString))
            {
                akka.WithSqlPersistence(
                    connectionString,
                    providerName: LinqToDB.ProviderName.PostgreSQL,
                    mode: PersistenceMode.Both,
                    schemaName: PositionPersistenceSchema.SchemaName,
                    autoInitialize: true);
            }
        });

        // Cluster Sharding for the PositionActor is a role-conditional workload (US-F0-06-T04b):
        // the host starts it only on nodes that declare the agents role, through the existing
        // IRoleWorkload seam. The entity Props seam now supplies the persistent PositionActor
        // (US-F0-06-T06b); TryAdd keeps the wiring replaceable for later entity behaviour.
        builder.Services.TryAddSingleton<IAiAgentGatewayInvoker, AiAgentGatewayInvoker>();
        builder.Services.TryAddSingleton<IPositionEntityProps, PositionEntityProps>();
        builder.Services.TryAddSingleton<ISchedulerPulseDispatcher>(
            AkkaClusterShardingSchedulerPulseDispatcher.Instance);
        builder.Services.AddSingleton<PositionShardingWorkload>();
        builder.Services.AddSingleton<IRoleWorkload>(
            sp => sp.GetRequiredService<PositionShardingWorkload>());

        // The SchedulerCoordinator is a cluster-wide logical singleton (US-F0-09-T03c, ADR-004):
        // it is materialized exactly once across the agents role through the same IRoleWorkload seam
        // so two nodes never materialize the same schedules in parallel.
        builder.Services.AddSingleton<SchedulerCoordinatorSingletonWorkload>();
        builder.Services.AddSingleton<IRoleWorkload>(
            sp => sp.GetRequiredService<SchedulerCoordinatorSingletonWorkload>());

        return builder;
    }
}
