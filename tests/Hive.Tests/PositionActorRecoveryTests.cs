using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T06b: the persistent PositionActor restores a snapshot and replays the
/// remaining journal before accepting new position commands.
/// </summary>
public sealed class PositionActorRecoveryTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Position_actor_restores_snapshot_then_replays_later_events_before_commands()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From($"bug-triage-recovery-{Guid.NewGuid():N}"));
        var persistenceId = PositionActor.PersistenceIdFor(entity.Value);
        var snapshot = SampleSnapshot();
        Assert.Equal(new[] { snapshot.Inbox[0].Id }, snapshot.RecentHistory);
        var replayed = new ShortMemoryUpdated("after-snapshot", "replayed", At.AddMinutes(1));
        var system = ActorSystem.Create(
            $"position-recovery-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString("""
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                akka.actor {
                  serializers {
                    hive-position-protocol = "Hive.Actors.Serialization.PositionProtocolJsonSerializer, Hive.Actors"
                  }
                  serialization-bindings {
                    "Hive.Domain.Positions.PositionEvent, Hive.Domain" = hive-position-protocol
                    "Hive.Domain.Positions.PositionSnapshot, Hive.Domain" = hive-position-protocol
                  }
                }
                """));

        try
        {
            var seeder = system.ActorOf(
                Props.Create(() => new PositionActorSeedPersistenceActor(persistenceId)),
                "position-recovery-seeder");

            await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
            await seeder.Ask<EventSeeded>(new SeedEvent(replayed), Timeout());
            await seeder.GracefulStop(Timeout());

            var provider = LoadedProvider(entity, new PositionConfigurationStamp(1, "sha256:v1"));
            var occupantFactory = new IgnoringOccupantFactory();
            var publisher = new CapturingProjectionPublisher();
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    occupantFactory,
                    publisher,
                    () => At.AddMinutes(2))),
                "position-recovery-reader");

            await WaitForReadyAsync(actor);
            var recovered = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(new[] { snapshot.Inbox[0].Id }, recovered.Inbox.Select(message => message.Id));
            Assert.Equal(new[] { snapshot.Inbox[0].Id }, recovered.RecentHistory);
            Assert.Equal(snapshot.OpenTasks[0].TaskId, Assert.Single(recovered.OpenTasks).Key);
            Assert.Equal("snapshot-context", recovered.ShortMemory["current-thread"]);
            Assert.Equal("replayed", recovered.ShortMemory["after-snapshot"]);
            Assert.Equal(snapshot.Occupant, recovered.Occupant);
            Assert.Equal(snapshot.OccupantType, recovered.OccupantType);
            Assert.Contains(snapshot.ProcessedMessages[0], recovered.ProcessedMessages);

            actor.Tell(new UpdateShortMemory("after-command", "accepted"));
            var afterCommand = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal("accepted", afterCommand.ShortMemory["after-command"]);

            var retained = SampleRetainedAction(entity, "directive:retained");
            actor.Tell(new RetainAction(retained));
            actor.Tell(new RetainAction(SampleRetainedAction(
                entity,
                retained.CorrelationId,
                RetainedActionId.New())));
            var afterRetention = await WaitForStateAsync(
                actor,
                state => state.RetainedActions.Count == 1);
            Assert.Equal(retained, Assert.Single(afterRetention.RetainedActions).Value);
            Assert.Contains(
                publisher.Events,
                @event => @event is PositionRetainedActionReady ready && ready.Action == retained);

            await actor.GracefulStop(Timeout());

            var restartPublisher = new CapturingProjectionPublisher();
            var restarted = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    occupantFactory,
                    restartPublisher,
                    () => At.AddMinutes(3))),
                "position-recovery-restarted");

            await WaitForReadyAsync(restarted);
            var afterRestart = await restarted.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal("accepted", afterRestart.ShortMemory["after-command"]);
            Assert.Equal(retained, Assert.Single(afterRestart.RetainedActions).Value);
            Assert.DoesNotContain(
                restartPublisher.Events,
                @event => @event is PositionRetainedActionReady);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Recovered_pending_message_delivered_to_real_occupant_is_completed_and_persisted()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From($"bug-triage-recovery-real-occupant-{Guid.NewGuid():N}"));
        var persistenceId = PositionActor.PersistenceIdFor(entity.Value);
        var snapshot = SampleSnapshot();
        var replayed = new ShortMemoryUpdated("after-snapshot", "replayed", At.AddMinutes(1));
        var system = ActorSystem.Create(
            $"position-recovery-real-occupant-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString("""
                akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                akka.actor {
                  serializers {
                    hive-position-protocol = "Hive.Actors.Serialization.PositionProtocolJsonSerializer, Hive.Actors"
                  }
                  serialization-bindings {
                    "Hive.Domain.Positions.PositionEvent, Hive.Domain" = hive-position-protocol
                    "Hive.Domain.Positions.PositionSnapshot, Hive.Domain" = hive-position-protocol
                  }
                }
                """));

        try
        {
            var seeder = system.ActorOf(
                Props.Create(() => new PositionActorSeedPersistenceActor(persistenceId)),
                "position-recovery-real-occupant-seeder");

            await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
            await seeder.Ask<EventSeeded>(new SeedEvent(replayed), Timeout());
            await seeder.GracefulStop(Timeout());

            var provider = LoadedProvider(entity, new PositionConfigurationStamp(1, "sha256:v1"));
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At.AddMinutes(2))),
                "position-recovery-real-occupant-reader");

            await WaitForReadyAsync(actor);
            var completed = await WaitForStateAsync(actor, state => state.Inbox.IsEmpty);

            Assert.Equal(new[] { snapshot.Inbox[0].Id }, completed.RecentHistory);
            Assert.Contains(snapshot.ProcessedMessages[0], completed.ProcessedMessages);
            Assert.Equal("replayed", completed.ShortMemory["after-snapshot"]);

            await actor.GracefulStop(Timeout());

            var restarted = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At.AddMinutes(3))),
                "position-recovery-real-occupant-restarted");

            await WaitForReadyAsync(restarted);
            var afterRestart = await restarted.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Empty(afterRestart.Inbox);
            Assert.Equal(new[] { snapshot.Inbox[0].Id }, afterRestart.RecentHistory);
            Assert.Contains(snapshot.ProcessedMessages[0], afterRestart.ProcessedMessages);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static PositionSnapshot SampleSnapshot()
    {
        var message = SampleMessage(MessageId("aaaaaaaa-0000-0000-0000-000000000101"));
        return new PositionSnapshot(
            takenAt: At,
            occupant: OccupantId.From("agent-7"),
            occupantType: OccupantType.AiAgent,
            inbox: new[] { message },
            openTasks: new[]
            {
                new PersistedTask(
                    PositionTaskId.From(new Guid("cccccccc-0000-0000-0000-000000000101")),
                    ThreadId(),
                    "triage incoming bug",
                    Priority.High,
                    At,
                    At.AddHours(2),
                    message.Id),
            },
            shortMemory: new Dictionary<string, string> { ["current-thread"] = "snapshot-context" },
            recentHistory: new[] { message.Id },
            processedMessages: new[] { message.Id });
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

    private static PersistedRetainedAction SampleRetainedAction(
        PositionEntityId entity,
        string correlationId,
        RetainedActionId? id = null) =>
        new(
            id ?? RetainedActionId.New(),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000003"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"title\":\"Regression\"}",
            "{}",
            correlationId,
            entity.Organization,
            entity.Position,
            ThreadId(),
            MessageId("aaaaaaaa-0000-0000-0000-000000000199"),
            DirectiveId.From(new Guid("dddddddd-0000-0000-0000-000000000199")),
            DirectiveId.From(new Guid("eeeeeeee-0000-0000-0000-000000000199")),
            "action-gate-escalation-required",
            At.AddMinutes(2));

    private static ThreadId ThreadId() =>
        Hive.Domain.Identity.ThreadId.From(new Guid("bbbbbbbb-0000-0000-0000-000000000101"));

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private sealed class CapturingProjectionPublisher : IPositionProjectionPublisher
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<PositionProjectionEvent> _events = new();

        public IReadOnlyCollection<PositionProjectionEvent> Events => _events.ToArray();

        public void Publish(PositionProjectionEvent @event) => _events.Enqueue(@event);
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
            catch (AskTimeoutException) when (DateTimeOffset.UtcNow < deadline)
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

    private static async Task<PositionState> WaitForStateAsync(
        IActorRef actor,
        Func<PositionState, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        PositionState? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                latest = await actor.Ask<PositionState>(
                    GetPositionState.Instance,
                    TimeSpan.FromSeconds(1));
            }
            catch (AskTimeoutException) when (DateTimeOffset.UtcNow < deadline)
            {
                await Task.Delay(25);
                continue;
            }

            if (predicate(latest))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException($"PositionActor state did not match predicate. Latest state: {latest}.");
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

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class IgnoringOccupantFactory : IPositionOccupantFactory
    {
        public Props Create(OccupantId occupant, OccupantType occupantType) =>
            Props.Create<IgnoringOccupantActor>();
    }

    private sealed class IgnoringOccupantActor : ReceiveActor
    {
        public IgnoringOccupantActor()
        {
            ReceiveAny(_ =>
            {
            });
        }
    }

    private sealed class PositionActorSeedPersistenceActor : ReceivePersistentActor
    {
        private IActorRef? _snapshotReplyTo;

        public PositionActorSeedPersistenceActor(string persistenceId)
        {
            PersistenceId = persistenceId;

            RecoverAny(_ =>
            {
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
            Command<SeedEvent>(command =>
            {
                var replyTo = Sender;
                Persist(command.Event, _ => replyTo.Tell(EventSeeded.Instance));
            });
        }

        public override string PersistenceId { get; }
    }

    private sealed record SeedSnapshot(PositionSnapshot Snapshot);

    private sealed record SnapshotSeeded
    {
        public static SnapshotSeeded Instance { get; } = new();
    }

    private sealed record SeedEvent(PositionEvent Event);

    private sealed record EventSeeded
    {
        public static EventSeeded Instance { get; } = new();
    }
}
