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
