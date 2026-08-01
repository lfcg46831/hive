using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T07: redelivered messages already recorded in recovered state are suppressed
/// without duplicating inbox work or persisted message events.
/// </summary>
public sealed class PositionActorIdempotencyTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 26, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Redelivered_message_from_recovered_processed_set_does_not_persist_duplicate_event()
    {
        var entity = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));
        var persistenceId = PositionActor.PersistenceIdFor(entity.Value);
        var message = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000201"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000201"));
        var snapshot = new PositionSnapshot(
            At,
            inbox: new[] { message },
            processedMessages: new[] { message.Id });
        var system = ActorSystem.Create(
            $"position-idempotency-{Guid.NewGuid():N}",
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
                Props.Create(() => new PositionActorPersistenceProbe(persistenceId)),
                "position-idempotency-seeder");

            await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
            await seeder.GracefulStop(Timeout());

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, new PositionConfigurationStamp(1, "sha256:v1")),
                    () => At.AddMinutes(1))),
                "position-idempotency-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(message));

            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(message.Id, Assert.Single(state.Inbox).Id);
            Assert.Equal(message.Id, Assert.Single(state.ProcessedMessages));

            await actor.GracefulStop(Timeout());

            var probe = system.ActorOf(
                Props.Create(() => new PositionActorPersistenceProbe(persistenceId)),
                "position-idempotency-probe");

            var persistedEvents = await probe.Ask<IReadOnlyList<PositionEvent>>(ReadEvents.Instance, Timeout());

            Assert.Empty(persistedEvents.OfType<MessageReceived>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Checkpoint_revisions_are_idempotent_and_replay_after_restart()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("checkpoint-idempotency"));
        var persistenceId = PositionActor.PersistenceIdFor(entity.Value);
        var system = ActorSystem.Create(
            $"position-checkpoint-idempotency-{Guid.NewGuid():N}",
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
            var provider = LoadedProvider(entity, new PositionConfigurationStamp(1, "sha256:v1"));
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    () => At.AddMinutes(1))),
                "position-checkpoint-idempotency-actor");
            await WaitForReadyAsync(actor);

            var first = Checkpoint(entity, revision: 1, completed: ["inspect"], next: "verify");
            var second = Checkpoint(
                entity,
                revision: 2,
                completed: ["inspect", "verify"],
                next: null);
            actor.Tell(new PersistDirectiveCheckpoint(first));
            actor.Tell(new PersistDirectiveCheckpoint(first));
            actor.Tell(new PersistDirectiveCheckpoint(second));
            actor.Tell(new PersistDirectiveCheckpoint(first));
            actor.Tell(new PersistDirectiveCheckpoint(Checkpoint(
                entity,
                revision: 4,
                completed: ["inspect", "verify"],
                next: null)));

            var state = await WaitForCheckpointAsync(actor, revision: 2);
            Assert.Equal(second, Assert.Single(state.DirectiveCheckpoints).Value);

            await actor.GracefulStop(Timeout());

            var probe = system.ActorOf(
                Props.Create(() => new PositionActorPersistenceProbe(persistenceId)),
                "position-checkpoint-idempotency-probe");
            var persistedEvents = await probe.Ask<IReadOnlyList<PositionEvent>>(
                ReadEvents.Instance,
                Timeout());

            Assert.Equal(
                [1, 2],
                persistedEvents
                    .OfType<DirectiveCheckpointPersisted>()
                    .Select(@event => @event.Checkpoint.Revision));
            await probe.GracefulStop(Timeout());

            var restarted = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    () => At.AddMinutes(2))),
                "position-checkpoint-idempotency-restarted");
            await WaitForReadyAsync(restarted);

            var recovered = await restarted.Ask<PositionState>(
                GetPositionState.Instance,
                Timeout());

            Assert.Equal(second, Assert.Single(recovered.DirectiveCheckpoints).Value);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static Memo SampleMessage(MessageId id, ThreadId thread) =>
        new(
            id,
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            thread,
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Customer reported a regression.");

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static ThreadId ThreadId(string value) =>
        Hive.Domain.Identity.ThreadId.From(new Guid(value));

    private static DirectiveCheckpoint Checkpoint(
        PositionEntityId entity,
        int revision,
        IEnumerable<string> completed,
        string? next)
    {
        var plan = new DirectiveCheckpointPlan(
            DirectiveCheckpointContractVersions.V1,
            [
                new DirectiveCheckpointSubtask(
                    1,
                    "inspect",
                    "Inspect the work",
                    ["inspection recorded"],
                    TimeSpan.FromMinutes(1)),
                new DirectiveCheckpointSubtask(
                    2,
                    "verify",
                    "Verify the work",
                    ["verification recorded"],
                    TimeSpan.FromMinutes(2)),
            ]);
        var correlation = new DirectiveCheckpointCorrelation(
            entity.Organization,
            entity.Position,
            ThreadId("bbbbbbbb-0000-0000-0000-000000000301"),
            DirectiveId.From(new Guid("dddddddd-0000-0000-0000-000000000301")));

        return new DirectiveCheckpoint(
            DirectiveCheckpointContractVersions.V1,
            revision,
            plan,
            correlation,
            completed.Select(id => new CompletedDirectiveCheckpointSubtask(
                id,
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.PersistedState,
                    $"state.{id}")])),
            nextSubtaskId: next);
    }

    private static async Task WaitForReadyAsync(IActorRef actor)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            try
            {
                var status = await actor.Ask<PositionRuntimeStatus>(
                    GetPositionRuntimeStatus.Instance,
                    TimeSpan.FromSeconds(1));
                if (status.OperationalState == PositionOperationalState.Ready)
                {
                    return;
                }
            }
            catch (AskTimeoutException) when (DateTimeOffset.UtcNow < deadline)
            {
                // Recovery may temporarily delay replies when the shared test runner is busy.
                // Keep polling until the outer readiness deadline is exhausted.
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("PositionActor did not reach Ready.");
    }

    private static async Task<PositionState> WaitForCheckpointAsync(
        IActorRef actor,
        int revision)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        PositionState? latest = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await actor.Ask<PositionState>(
                GetPositionState.Instance,
                TimeSpan.FromSeconds(1));
            if (latest.DirectiveCheckpoints.Values.Any(checkpoint =>
                    checkpoint.Revision == revision))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PositionActor did not persist checkpoint revision {revision}. Latest state: {latest}.");
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

    private sealed class PositionActorPersistenceProbe : ReceivePersistentActor
    {
        private readonly List<PositionEvent> _events = new();
        private IActorRef? _snapshotReplyTo;

        public PositionActorPersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;

            Recover<PositionEvent>(_events.Add);
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
            Command<ReadEvents>(_ => Sender.Tell(_events.ToArray()));
        }

        public override string PersistenceId { get; }
    }

    private sealed record SeedSnapshot(PositionSnapshot Snapshot);

    private sealed record SnapshotSeeded
    {
        public static SnapshotSeeded Instance { get; } = new();
    }

    private sealed record ReadEvents
    {
        public static ReadEvents Instance { get; } = new();
    }
}
