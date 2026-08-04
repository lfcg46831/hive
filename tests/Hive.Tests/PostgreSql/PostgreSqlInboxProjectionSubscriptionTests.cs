using System.Net;
using System.Net.Sockets;
using Akka.Actor;
using Akka.Persistence;
using Akka.Pattern;
using Hive.Actors;
using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing.PostgreSql;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.PostgreSql;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlInboxProjectionSubscriptionTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 8, 4, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Hosted_subscriptions_capture_all_sources_and_resume_after_restart()
    {
        await fixture.ResetRegistryAsync();
        await fixture.ResetPersistenceAsync();
        await fixture.ResetInboxAsync();
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
                await AppendAuditRecordAsync(ordinal: 1);
                await WaitForCapturedFactsAsync(expectedCount: 3);
            }
            finally
            {
                await firstHost.StopAsync();
            }
        }

        (long Position, long Audit) firstCheckpoints = await ReadCheckpointsAsync();

        using (var restartedHost = BuildHost(GetFreeTcpPort()))
        {
            await restartedHost.StartAsync();
            try
            {
                await SeedAsync(
                    restartedHost.Services.GetRequiredService<ActorSystem>(),
                    entityId,
                    new MessageReceived(Message(entityId, ordinal: 2), OccurredAt.AddMinutes(1)));
                await AppendAuditRecordAsync(ordinal: 2);
                await WaitForCapturedFactsAsync(expectedCount: 6);
            }
            finally
            {
                await restartedHost.StopAsync();
            }
        }

        (long Position, long Audit) restartedCheckpoints = await ReadCheckpointsAsync();
        Assert.True(restartedCheckpoints.Position > firstCheckpoints.Position);
        Assert.True(restartedCheckpoints.Audit > firstCheckpoints.Audit);

        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT source, count(*)
            FROM inbox.projection_facts
            GROUP BY source
            ORDER BY source;
            """);
        await using var reader = await command.ExecuteReaderAsync();
        var counts = new List<(string Source, long Count)>();
        while (await reader.ReadAsync())
        {
            counts.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        Assert.Equal(
            [
                ("AuditLog", 2L),
                ("OrganizationalMessage", 2L),
                ("PositionEvent", 2L),
            ],
            counts);
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
        builder.AddHiveInboxProjection();
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
            $"inbox-projection-seed-{Guid.NewGuid():N}");
        await actor.Ask<EventSeeded>(new SeedEvent(@event), TimeSpan.FromSeconds(5));
        await actor.GracefulStop(TimeSpan.FromSeconds(5));
    }

    private async Task AppendAuditRecordAsync(int ordinal)
    {
        await using var auditLog = new PostgreSqlJourneyAuditLog(fixture.ConnectionString);
        auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.PositionAccepted,
            JourneyAuditOutcome.Accepted,
            OrganizationId.From("acme-delivery"),
            ThreadId.From(Guid.Parse($"40000000-0000-0000-0000-{ordinal:D12}")),
            MessageId.From(Guid.Parse($"50000000-0000-0000-0000-{ordinal:D12}")),
            positionId: PositionId.From("delivery-lead"),
            messageType: "Memo",
            occurredAtUtc: OccurredAt.AddMinutes(ordinal),
            idempotencyDiscriminator: $"inbox-subscription-{ordinal}"));
    }

    private async Task WaitForCapturedFactsAsync(long expectedCount)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(15);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await using var dataSource = fixture.CreateDataSource();
            await using var command = dataSource.CreateCommand(
                "SELECT count(*) FROM inbox.projection_facts;");
            if ((long)(await command.ExecuteScalarAsync())! >= expectedCount)
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException(
            $"Inbox projection did not capture {expectedCount} facts before timeout.");
    }

    private async Task<(long Position, long Audit)> ReadCheckpointsAsync()
    {
        await using var dataSource = fixture.CreateDataSource();
        await using var command = dataSource.CreateCommand(
            """
            SELECT
                (SELECT source_offset
                 FROM inbox.projection_checkpoints
                 WHERE subscription = 'PositionJournal'),
                (SELECT source_offset
                 FROM inbox.projection_checkpoints
                 WHERE subscription = 'AuditLog');
            """);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return (reader.GetInt64(0), reader.GetInt64(1));
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
            $"Inbox projection message {ordinal}");

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
