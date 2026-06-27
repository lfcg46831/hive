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
        var entity = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));
        var persistenceId = PositionActor.PersistenceIdFor(entity.Value);
        var snapshot = SampleSnapshot();
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
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At.AddMinutes(2))),
                "position-recovery-reader");

            await WaitForReadyAsync(actor);
            var recovered = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(snapshot.Inbox[0].Id, Assert.Single(recovered.Inbox).Id);
            Assert.Equal(snapshot.OpenTasks[0].TaskId, Assert.Single(recovered.OpenTasks).Key);
            Assert.Equal("snapshot-context", recovered.ShortMemory["current-thread"]);
            Assert.Equal("replayed", recovered.ShortMemory["after-snapshot"]);
            Assert.Equal(snapshot.Occupant, recovered.Occupant);
            Assert.Equal(snapshot.OccupantType, recovered.OccupantType);
            Assert.Contains(snapshot.ProcessedMessages[0], recovered.ProcessedMessages);

            actor.Tell(new UpdateShortMemory("after-command", "accepted"));
            var afterCommand = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal("accepted", afterCommand.ShortMemory["after-command"]);

            await actor.GracefulStop(Timeout());

            var restarted = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At.AddMinutes(3))),
                "position-recovery-restarted");

            await WaitForReadyAsync(restarted);
            var afterRestart = await restarted.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal("accepted", afterRestart.ShortMemory["after-command"]);
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
            At,
            OccupantId.From("agent-7"),
            OccupantType.AiAgent,
            new[] { message },
            new[]
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
            new Dictionary<string, string> { ["current-thread"] = "snapshot-context" },
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
        Hive.Domain.Identity.ThreadId.From(new Guid("bbbbbbbb-0000-0000-0000-000000000101"));

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static async Task WaitForReadyAsync(IActorRef actor)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var status = await actor.Ask<PositionRuntimeStatus>(
                GetPositionRuntimeStatus.Instance,
                TimeSpan.FromSeconds(1));
            if (status.OperationalState == PositionOperationalState.Ready)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("PositionActor did not reach Ready.");
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
                canDecide: Array.Empty<string>(),
                mustEscalate: Array.Empty<string>(),
                requiresHumanApproval: Array.Empty<string>()));

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
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
