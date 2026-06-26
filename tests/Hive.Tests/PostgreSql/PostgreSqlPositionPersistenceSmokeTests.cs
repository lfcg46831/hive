using Akka.Actor;
using Akka.Persistence;
using Hive.Actors;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests.PostgreSql;

/// <summary>
/// Verifies US-F0-06-T05c: PositionActor protocol events and snapshots can be persisted to and
/// recovered from the configured PostgreSQL Akka.Persistence journal/snapshot stores.
/// </summary>
[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionPersistenceSmokeTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 6, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Position_protocol_events_and_snapshots_persist_and_recover_through_postgresql()
    {
        await fixture.ResetRegistryAsync();
        await fixture.ResetPersistenceAsync();

        var persistenceId = $"position-smoke-{Guid.NewGuid():N}";
        var first = new MessageReceived(SampleMessage(MessageId("aaaaaaaa-0000-0000-0000-000000000001")), At);
        var snapshot = SampleSnapshot();
        var second = new MessageReceived(SampleMessage(MessageId("aaaaaaaa-0000-0000-0000-000000000002")), At.AddMinutes(1));

        using var host = BuildHost(GetFreeTcpPort());
        await host.StartAsync();
        try
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var writer = system.ActorOf(
                Props.Create(() => new PositionPersistenceSmokeActor(persistenceId)),
                "position-persistence-writer");

            await writer.Ask<EventRecorded>(new RecordEvent(first), Timeout());
            await writer.Ask<SnapshotRecorded>(new RecordSnapshot(snapshot), Timeout());
            await writer.Ask<EventRecorded>(new RecordEvent(second), Timeout());
            await writer.GracefulStop(Timeout());

            var reader = system.ActorOf(
                Props.Create(() => new PositionPersistenceSmokeActor(persistenceId)),
                "position-persistence-reader");

            var recovered = await reader.Ask<RecoveredState>(QueryRecovered.Instance, Timeout());

            Assert.NotNull(recovered.Snapshot);
            Assert.Equal(snapshot.TakenAt, recovered.Snapshot.TakenAt);
            Assert.Equal(snapshot.Inbox[0].Id, recovered.Snapshot.Inbox[0].Id);
            Assert.Equal(snapshot.ProcessedMessages[0], recovered.Snapshot.ProcessedMessages[0]);
            var recoveredEvent = Assert.Single(recovered.Events);
            Assert.Equal(second.Message.Id, recoveredEvent.Message.Id);
            Assert.Equal(second.OccurredAt, recoveredEvent.OccurredAt);
        }
        finally
        {
            await host.StopAsync();
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
        return builder.Build();
    }

    private static PositionSnapshot SampleSnapshot()
    {
        var message = SampleMessage(MessageId("aaaaaaaa-0000-0000-0000-000000000003"));
        return new PositionSnapshot(
            At,
            OccupantId.From("agent-7"),
            OccupantType.AiAgent,
            new[] { message },
            new[]
            {
                new PersistedTask(
                    PositionTaskId.From(new Guid("cccccccc-0000-0000-0000-000000000001")),
                    ThreadId(),
                    "triage incoming bug",
                    Priority.High,
                    At,
                    At.AddHours(2),
                    message.Id),
            },
            new Dictionary<string, string> { ["current-thread"] = "customer-impact" },
            new[] { message.Id },
            new[] { message.Id });
    }

    private static Memo SampleMessage(MessageId id) =>
        new(
            id,
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            ThreadId(),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Customer reported a regression.");

    private static ThreadId ThreadId() =>
        Hive.Domain.Identity.ThreadId.From(new Guid("bbbbbbbb-0000-0000-0000-000000000001"));

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

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

    private sealed class PositionPersistenceSmokeActor : ReceivePersistentActor
    {
        private readonly List<MessageReceived> _events = new();

        private IActorRef? _snapshotReplyTo;
        private PositionSnapshot? _snapshot;

        public PositionPersistenceSmokeActor(string persistenceId)
        {
            PersistenceId = persistenceId;

            Recover<SnapshotOffer>(offer =>
            {
                _snapshot = (PositionSnapshot)offer.Snapshot;
            });
            Recover<MessageReceived>(_events.Add);

            Command<RecordEvent>(command =>
            {
                var replyTo = Sender;
                Persist(command.Event, persisted =>
                {
                    _events.Add(persisted);
                    replyTo.Tell(new EventRecorded(persisted.Message.Id));
                });
            });
            Command<RecordSnapshot>(command =>
            {
                _snapshotReplyTo = Sender;
                SaveSnapshot(command.Snapshot);
            });
            Command<SaveSnapshotSuccess>(_ =>
            {
                _snapshotReplyTo?.Tell(SnapshotRecorded.Instance);
                _snapshotReplyTo = null;
            });
            Command<SaveSnapshotFailure>(failure =>
            {
                _snapshotReplyTo?.Tell(new Status.Failure(failure.Cause));
                _snapshotReplyTo = null;
            });
            Command<QueryRecovered>(_ =>
            {
                Sender.Tell(new RecoveredState(_snapshot, _events.ToArray()));
            });
        }

        public override string PersistenceId { get; }
    }

    private sealed record RecordEvent(MessageReceived Event);

    private sealed record EventRecorded(MessageId MessageId);

    private sealed record RecordSnapshot(PositionSnapshot Snapshot);

    private sealed record SnapshotRecorded
    {
        public static SnapshotRecorded Instance { get; } = new();
    }

    private sealed record QueryRecovered
    {
        public static QueryRecovered Instance { get; } = new();
    }

    private sealed record RecoveredState(PositionSnapshot? Snapshot, IReadOnlyList<MessageReceived> Events);
}
