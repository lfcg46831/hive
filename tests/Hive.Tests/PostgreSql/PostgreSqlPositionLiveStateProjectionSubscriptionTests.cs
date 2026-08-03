using System.Net;
using System.Net.Sockets;
using Akka.Actor;
using Akka.Persistence;
using Akka.Pattern;
using Hive.Actors;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.PostgreSql;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionLiveStateProjectionSubscriptionTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 3, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hosted_subscription_captures_committed_events_and_resumes_after_restart()
    {
        await fixture.ResetRegistryAsync();
        await fixture.ResetPersistenceAsync();
        await DropAuditAsync();
        var entityId = PositionEntityId.Parse("acme-delivery/delivery-lead");

        using (var firstHost = BuildHost(GetFreeTcpPort()))
        {
            await firstHost.StartAsync();
            try
            {
                await SeedAsync(
                    firstHost.Services.GetRequiredService<ActorSystem>(),
                    entityId,
                    new MessageReceived(Message(entityId, ordinal: 1), OccurredAt));
                await WaitForCapturedPositionFactsAsync(expectedCount: 2);
            }
            finally
            {
                await firstHost.StopAsync();
            }
        }

        long checkpointAfterFirstHost;
        await using (var dataSource = fixture.CreateDataSource())
        await using (var command = dataSource.CreateCommand(
            """
            SELECT source_offset
            FROM organogram.position_state_projection_checkpoints
            WHERE subscription = 'PositionJournal';
            """))
        {
            checkpointAfterFirstHost = (long)(await command.ExecuteScalarAsync())!;
        }

        using (var restartedHost = BuildHost(GetFreeTcpPort()))
        {
            await restartedHost.StartAsync();
            try
            {
                await SeedAsync(
                    restartedHost.Services.GetRequiredService<ActorSystem>(),
                    entityId,
                    new MessageReceived(Message(entityId, ordinal: 2), OccurredAt.AddMinutes(1)));
                await WaitForCapturedPositionFactsAsync(expectedCount: 4);
            }
            finally
            {
                await restartedHost.StopAsync();
            }
        }

        await using (var dataSource = fixture.CreateDataSource())
        await using (var command = dataSource.CreateCommand(
            """
            SELECT source_offset,
                   (SELECT count(*)
                    FROM organogram.position_state_projection_facts
                    WHERE source = 'PositionEvent'),
                   (SELECT count(*)
                    FROM organogram.position_state_projection_facts
                    WHERE source = 'OrganizationalMessage')
            FROM organogram.position_state_projection_checkpoints
            WHERE subscription = 'PositionJournal';
            """))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert.True(await reader.ReadAsync());
            Assert.True(reader.GetInt64(0) > checkpointAfterFirstHost);
            Assert.Equal(2, reader.GetInt64(1));
            Assert.Equal(2, reader.GetInt64(2));
        }
    }

    private IHost BuildHost(int port)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = port.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["Hive:Node:Roles:0"] = NodeRoleNames.Api,
            ["Hive:Organizations:RootPath"] = Path.Combine(
                RepositoryRoot,
                "config",
                "organizations"),
            ["ConnectionStrings:PostgreSql"] = fixture.ConnectionString,
        });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        builder.AddHivePositionLiveStateProjection();
        return builder.Build();
    }

    private static async Task SeedAsync(
        ActorSystem system,
        PositionEntityId entityId,
        PositionEvent @event)
    {
        var actor = system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(
                PositionActor.PersistenceIdFor(entityId.Value))),
            $"position-projection-seed-{Guid.NewGuid():N}");
        await actor.Ask<EventSeeded>(new SeedEvent(@event), TimeSpan.FromSeconds(5));
        await actor.GracefulStop(TimeSpan.FromSeconds(5));
    }

    private async Task WaitForCapturedPositionFactsAsync(long expectedCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var dataSource = fixture.CreateDataSource();
            await using var command = dataSource.CreateCommand(
                """
                SELECT count(*)
                FROM organogram.position_state_projection_facts
                WHERE source IN ('PositionEvent', 'OrganizationalMessage');
                """);
            if ((long)(await command.ExecuteScalarAsync())! >= expectedCount)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Position live-state projection did not capture {expectedCount} facts before timeout.");
    }

    private async Task DropAuditAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static Memo Message(PositionEntityId entityId, int ordinal) =>
        new(
            MessageId.From(Guid.Parse($"20000000-0000-0000-0000-{ordinal:D12}")),
            entityId.Organization,
            new PositionEndpointRef(PositionId.From("ceo")),
            new PositionEndpointRef(entityId.Position),
            ThreadId.From(Guid.Parse($"30000000-0000-0000-0000-{ordinal:D12}")),
            Priority.Normal,
            schemaVersion: 1,
            OccurredAt.AddMinutes(ordinal),
            deadline: null,
            $"Projection message {ordinal}");

    private static int GetFreeTcpPort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
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

    private sealed class PositionActorPersistenceProbe : ReceivePersistentActor
    {
        public PositionActorPersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            Command<SeedEvent>(seed =>
            {
                var replyTo = Sender;
                Persist(seed.Event, _ => replyTo.Tell(EventSeeded.Instance));
            });
        }

        public override string PersistenceId { get; }
    }

    private sealed record SeedEvent(PositionEvent Event);

    private sealed record EventSeeded
    {
        public static EventSeeded Instance { get; } = new();
    }
}
