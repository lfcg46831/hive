using Akka.Actor;
using Akka.Persistence;
using Hive.Actors;
using Hive.Actors.Positions;
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
/// Verifies US-F0-06-T13a: a restarted PositionActor rebuilds inbox, open tasks, short memory and
/// recent history from PostgreSQL through both full journal replay and snapshot restore.
/// </summary>
[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlPositionActorRecoveryTests(PostgreSqlFixture fixture)
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Restart_replays_full_journal_into_recoverable_position_state()
    {
        await fixture.ResetRegistryAsync();
        await fixture.ResetPersistenceAsync();

        var entity = EntityId("acme", "bug-triage-full-replay");
        var stamp = new PositionConfigurationStamp(1, "sha256:full-replay");
        var handled = SampleMessage(
            entity,
            MessageId("aaaaaaaa-0000-0000-0000-000000000601"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000601"));
        var pending = SampleMessage(
            entity,
            MessageId("aaaaaaaa-0000-0000-0000-000000000602"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000602"));
        var taskId = PositionTaskId.From(new Guid("cccccccc-0000-0000-0000-000000000601"));
        var deadline = At.AddHours(2);
        var revisedDeadline = At.AddHours(6);

        using (var firstHost = BuildHost(GetFreeTcpPort()))
        {
            await firstHost.StartAsync();
            try
            {
                var system = firstHost.Services.GetRequiredService<ActorSystem>();
                await SeedEventsAsync(
                    system,
                    entity,
                    new MessageReceived(handled, At),
                    new MessageDispatched(
                        handled.Id,
                        handled.Thread,
                        OccupantId.From("agent-7"),
                        OccupantType.AiAgent,
                        At.AddMinutes(1)),
                    new MessageProcessingCompleted(
                        "message-processing-completed:aaaaaaaa-0000-0000-0000-000000000601",
                        handled.Id,
                        handled.Thread,
                        MessageProcessingCompletionStatus.Completed,
                        At.AddMinutes(1).AddSeconds(1)),
                    new MessageReceived(pending, At.AddMinutes(2)),
                    new TaskCreated(
                        taskId,
                        pending.Thread,
                        "triage production regression",
                        Priority.High,
                        At.AddMinutes(3),
                        deadline,
                        pending.Id),
                    new TaskUpdated(
                        taskId,
                        "database owner is investigating",
                        At.AddMinutes(4),
                        Priority.Critical,
                        revisedDeadline),
                    new ShortMemoryUpdated("handoff", "customer is blocked", At.AddMinutes(5)),
                    new PositionConfigurationApplied(stamp, At.AddMinutes(6)));
            }
            finally
            {
                await firstHost.StopAsync();
            }
        }

        using var restartedHost = BuildHost(GetFreeTcpPort());
        await restartedHost.StartAsync();
        try
        {
            var system = restartedHost.Services.GetRequiredService<ActorSystem>();
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    () => At.AddMinutes(7))),
                "position-full-replay-reader");

            await WaitForReadyAsync(actor);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(new[] { pending.Id }, state.Inbox.Select(message => message.Id));
            Assert.Equal(new[] { handled.Id }, state.RecentHistory);
            Assert.Contains(handled.Id, state.ProcessedMessages);
            Assert.Contains(pending.Id, state.ProcessedMessages);
            Assert.Equal("customer is blocked", state.ShortMemory["handoff"]);

            var task = Assert.Single(state.OpenTasks).Value;
            Assert.Equal(taskId, task.TaskId);
            Assert.Equal(pending.Thread, task.Thread);
            Assert.Equal("triage production regression", task.Title);
            Assert.Equal(Priority.Critical, task.Priority);
            Assert.Equal(At.AddMinutes(3), task.OpenedAt);
            Assert.Equal(revisedDeadline, task.Deadline);
            Assert.Equal(pending.Id, task.CausedBy);

            await actor.GracefulStop(Timeout());
        }
        finally
        {
            await restartedHost.StopAsync();
        }
    }

    [Fact]
    public async Task Restart_restores_snapshot_then_replays_later_journal_events_from_postgresql()
    {
        await fixture.ResetRegistryAsync();
        await fixture.ResetPersistenceAsync();

        var entity = EntityId("acme", "bug-triage-snapshot-restore");
        var stamp = new PositionConfigurationStamp(1, "sha256:snapshot-restore");
        var historyMessage = SampleMessage(
            entity,
            MessageId("aaaaaaaa-0000-0000-0000-000000000701"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000701"));
        var snapshotPending = SampleMessage(
            entity,
            MessageId("aaaaaaaa-0000-0000-0000-000000000702"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000702"));
        var laterPending = SampleMessage(
            entity,
            MessageId("aaaaaaaa-0000-0000-0000-000000000703"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000703"));
        var taskId = PositionTaskId.From(new Guid("cccccccc-0000-0000-0000-000000000701"));
        var snapshotDeadline = At.AddHours(3);
        var laterDeadline = At.AddHours(8);
        var occupant = OccupantId.From("agent-7");

        var snapshot = new PositionSnapshot(
            At.AddMinutes(6),
            inbox: new[] { snapshotPending },
            openTasks: new[]
            {
                new PersistedTask(
                    taskId,
                    snapshotPending.Thread,
                    "triage snapshot incident",
                    Priority.High,
                    At.AddMinutes(3),
                    snapshotDeadline,
                    snapshotPending.Id),
            },
            shortMemory: new Dictionary<string, string>
            {
                ["handoff"] = "state captured in snapshot",
            },
            recentHistory: new[] { historyMessage.Id },
            processedMessages: new[] { historyMessage.Id, snapshotPending.Id },
            lastConfigurationStamp: stamp);

        using (var firstHost = BuildHost(GetFreeTcpPort()))
        {
            await firstHost.StartAsync();
            try
            {
                var system = firstHost.Services.GetRequiredService<ActorSystem>();
                var seeder = system.ActorOf(
                    Props.Create(() => new PositionActorPersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
                    "position-snapshot-restore-seeder");

                await seeder.Ask<EventSeeded>(new SeedEvent(new MessageReceived(historyMessage, At)), Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new MessageDispatched(
                        historyMessage.Id,
                        historyMessage.Thread,
                        occupant,
                        OccupantType.AiAgent,
                        At.AddMinutes(1))),
                    Timeout());
                await seeder.Ask<EventSeeded>(new SeedEvent(new MessageReceived(snapshotPending, At.AddMinutes(2))), Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new TaskCreated(
                        taskId,
                        snapshotPending.Thread,
                        "triage snapshot incident",
                        Priority.High,
                        At.AddMinutes(3),
                        snapshotDeadline,
                        snapshotPending.Id)),
                    Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new ShortMemoryUpdated(
                        "handoff",
                        "state captured in snapshot",
                        At.AddMinutes(4))),
                    Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new PositionConfigurationApplied(stamp, At.AddMinutes(5))),
                    Timeout());
                await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new TaskUpdated(
                        taskId,
                        "snapshot follow-up was replayed",
                        At.AddMinutes(7),
                        Priority.Critical,
                        laterDeadline)),
                    Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new ShortMemoryUpdated(
                        "after-snapshot",
                        "journal event after snapshot",
                        At.AddMinutes(8))),
                    Timeout());
                await seeder.Ask<EventSeeded>(
                    new SeedEvent(new MessageReceived(laterPending, At.AddMinutes(9))),
                    Timeout());
                await seeder.GracefulStop(Timeout());
            }
            finally
            {
                await firstHost.StopAsync();
            }
        }

        using var restartedHost = BuildHost(GetFreeTcpPort());
        await restartedHost.StartAsync();
        try
        {
            var system = restartedHost.Services.GetRequiredService<ActorSystem>();
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    () => At.AddMinutes(10))),
                "position-snapshot-restore-reader");

            await WaitForReadyAsync(actor);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(
                new[] { snapshotPending.Id, laterPending.Id },
                state.Inbox.Select(message => message.Id));
            Assert.Equal(new[] { historyMessage.Id }, state.RecentHistory);
            Assert.Contains(historyMessage.Id, state.ProcessedMessages);
            Assert.Contains(snapshotPending.Id, state.ProcessedMessages);
            Assert.Contains(laterPending.Id, state.ProcessedMessages);
            Assert.Equal("state captured in snapshot", state.ShortMemory["handoff"]);
            Assert.Equal("journal event after snapshot", state.ShortMemory["after-snapshot"]);

            var task = Assert.Single(state.OpenTasks).Value;
            Assert.Equal(taskId, task.TaskId);
            Assert.Equal(Priority.Critical, task.Priority);
            Assert.Equal(laterDeadline, task.Deadline);
            Assert.Equal(snapshotPending.Id, task.CausedBy);

            await actor.GracefulStop(Timeout());
        }
        finally
        {
            await restartedHost.StopAsync();
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

    private static async Task SeedEventsAsync(
        ActorSystem system,
        PositionEntityId entity,
        params PositionEvent[] events)
    {
        var seeder = system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
            $"seed-events-{Guid.NewGuid():N}");

        foreach (var @event in events)
        {
            await seeder.Ask<EventSeeded>(new SeedEvent(@event), Timeout());
        }

        await seeder.GracefulStop(Timeout());
    }

    private static async Task WaitForReadyAsync(IActorRef actor)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        PositionRuntimeStatus? latest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                latest = await actor.Ask<PositionRuntimeStatus>(
                    GetPositionRuntimeStatus.Instance,
                    TimeSpan.FromSeconds(1));
            }
            catch (AskTimeoutException)
            {
                await Task.Delay(25);
                continue;
            }

            if (latest.OperationalState == PositionOperationalState.Ready)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PositionActor did not reach Ready. Last observed state was {latest?.OperationalState}.");
    }

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp) =>
        new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Loaded(RuntimeConfiguration(entity, stamp)));

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        PositionEntityId entity,
        PositionConfigurationStamp stamp) =>
        new(
            stamp,
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("cto"),
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "engineer-v1",
                ai: null,
                workingHours: null,
                subscriptions: Array.Empty<SubscriptionConfiguration>(),
                tools: Array.Empty<ToolConfiguration>()),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: Array.Empty<string>()));

    private static Memo SampleMessage(
        PositionEntityId entity,
        MessageId id,
        ThreadId thread) =>
        new(
            id,
            entity.Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(entity.Position),
            thread,
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Customer reported a regression.");

    private static PositionEntityId EntityId(string organization, string position) =>
        PositionEntityId.From(OrganizationId.From(organization), PositionId.From(position));

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static ThreadId ThreadId(string value) =>
        Hive.Domain.Identity.ThreadId.From(new Guid(value));

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(20);

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

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class PositionActorPersistenceProbe : ReceivePersistentActor
    {
        private IActorRef? _snapshotReplyTo;

        public PositionActorPersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;

            RecoverAny(_ =>
            {
            });
            Command<SeedEvent>(command =>
            {
                var replyTo = Sender;
                Persist(command.Event, _ => replyTo.Tell(EventSeeded.Instance));
            });
            Command<SeedSnapshot>(command =>
            {
                _snapshotReplyTo = Sender;
                SaveSnapshot(command.Snapshot);
            });
            Command<SaveSnapshotSuccess>(_ =>
            {
                _snapshotReplyTo?.Tell(SnapshotSeeded.Instance);
                _snapshotReplyTo = null;
            });
            Command<SaveSnapshotFailure>(failure =>
            {
                _snapshotReplyTo?.Tell(new Status.Failure(failure.Cause));
                _snapshotReplyTo = null;
            });
        }

        public override string PersistenceId { get; }
    }

    private sealed record SeedEvent(PositionEvent Event);

    private sealed record EventSeeded
    {
        public static EventSeeded Instance { get; } = new();
    }

    private sealed record SeedSnapshot(PositionSnapshot Snapshot);

    private sealed record SnapshotSeeded
    {
        public static SnapshotSeeded Instance { get; } = new();
    }
}
