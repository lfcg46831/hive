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
/// Verifies US-F0-06-T10: PositionActor projection/audit signals are emitted only after recovered
/// state is available or after a new journal write has been confirmed.
/// </summary>
public sealed class PositionProjectionPublicationTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Committed_position_events_are_published_after_they_are_journaled()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var publisher = new CapturingProjectionPublisher();
        var system = CreateActorSystem("position-projection-commit");

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    publisher,
                    () => At.AddMinutes(1))),
                "position-projection-commit-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new UpdateShortMemory("handoff", "persisted"));

            var published = await publisher.WaitForAsync<PositionEventCommitted>(
                committed => committed.Event is ShortMemoryUpdated);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(entity, published.EntityId);
            var update = Assert.IsType<ShortMemoryUpdated>(published.Event);
            Assert.Equal("handoff", update.Key);
            Assert.Equal("persisted", state.ShortMemory["handoff"]);

            await actor.GracefulStop(Timeout());
            var persistedEvents = await ReadPersistedEventsAsync(system, entity);
            Assert.Contains(persistedEvents.OfType<ShortMemoryUpdated>(), e => e.Key == "handoff");
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Recovery_and_reactivation_are_published_without_republishing_replayed_events()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(2, "sha256:v2");
        var publisher = new CapturingProjectionPublisher();
        var system = CreateActorSystem("position-projection-recovery");

        try
        {
            await SeedEventAsync(
                system,
                entity,
                new ShortMemoryUpdated("replayed", "from-journal", At));
            await SeedEventAsync(
                system,
                entity,
                new PositionConfigurationApplied(stamp, At.AddMinutes(1)));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    publisher,
                    () => At.AddMinutes(2))),
                "position-projection-recovery-actor");

            var recovered = await publisher.WaitForAsync<PositionRecovered>();
            var reactivated = await publisher.WaitForAsync<PositionReactivated>();
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(entity, recovered.EntityId);
            Assert.Equal(stamp, recovered.LastConfigurationStamp);
            Assert.Equal(entity, reactivated.EntityId);
            Assert.Equal(stamp, reactivated.LastConfigurationStamp);
            Assert.Equal("from-journal", state.ShortMemory["replayed"]);
            Assert.DoesNotContain(
                publisher.Events.OfType<PositionEventCommitted>(),
                committed => committed.Event is ShortMemoryUpdated { Key: "replayed" });
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Duplicate_message_rejection_is_published_without_persisting_duplicate_work()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var message = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000401"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000401"));
        var publisher = new CapturingProjectionPublisher();
        var system = CreateActorSystem("position-projection-duplicate");

        try
        {
            await SeedSnapshotAsync(
                system,
                entity,
                new PositionSnapshot(
                    At,
                    inbox: new[] { message },
                    processedMessages: new[] { message.Id },
                    lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    publisher,
                    () => At.AddMinutes(1))),
                "position-projection-duplicate-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(message));

            var rejected = await publisher.WaitForAsync<PositionMessageDuplicateRejected>();
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(entity, rejected.EntityId);
            Assert.Equal(message.Id, rejected.Message);
            Assert.Equal(message.Thread, rejected.Thread);
            Assert.Equal(message.Id, Assert.Single(state.ProcessedMessages));

            await actor.GracefulStop(Timeout());
            var persistedEvents = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(persistedEvents.OfType<MessageReceived>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Configuration_rejection_is_published_without_business_side_effects()
    {
        var entity = EntityId("acme", "bug-triage");
        var publisher = new CapturingProjectionPublisher();
        var provider = new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Missing("position not found"));
        var system = CreateActorSystem("position-projection-config-blocked");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    publisher,
                    () => At)),
                "position-projection-config-blocked-actor");

            var rejected = await publisher.WaitForAsync<PositionConfigurationRejected>();

            Assert.Equal(entity, rejected.EntityId);
            Assert.Equal(PositionConfigurationBlockReason.ConfigurationMissing, rejected.Reason);
            Assert.Null(rejected.RecoveredStamp);
            Assert.Null(rejected.LoadedStamp);

            actor.Tell(new UpdateShortMemory("blocked", "must-not-persist"));
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.False(state.ShortMemory.ContainsKey("blocked"));
            Assert.DoesNotContain(
                publisher.Events.OfType<PositionEventCommitted>(),
                committed => committed.Event is ShortMemoryUpdated { Key: "blocked" });
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static ActorSystem CreateActorSystem(string namePrefix) =>
        ActorSystem.Create(
            $"{namePrefix}-{Guid.NewGuid():N}",
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

    private static async Task SeedSnapshotAsync(
        ActorSystem system,
        PositionEntityId entity,
        PositionSnapshot snapshot)
    {
        var seeder = system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
            $"seed-snapshot-{Guid.NewGuid():N}");
        await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
        await seeder.GracefulStop(Timeout());
    }

    private static async Task SeedEventAsync(
        ActorSystem system,
        PositionEntityId entity,
        PositionEvent @event)
    {
        var seeder = system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
            $"seed-event-{Guid.NewGuid():N}");
        await seeder.Ask<EventSeeded>(new SeedEvent(@event), Timeout());
        await seeder.GracefulStop(Timeout());
    }

    private static async Task<IReadOnlyList<PositionEvent>> ReadPersistedEventsAsync(
        ActorSystem system,
        PositionEntityId entity)
    {
        var probe = system.ActorOf(
            Props.Create(() => new PositionActorPersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
            $"read-events-{Guid.NewGuid():N}");
        var events = await probe.Ask<IReadOnlyList<PositionEvent>>(ReadEvents.Instance, Timeout());
        await probe.GracefulStop(Timeout());
        return events;
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

    private static PositionEntityId EntityId(string organization, string position) =>
        PositionEntityId.From(OrganizationId.From(organization), PositionId.From(position));

    private static MessageId MessageId(string value) =>
        Hive.Domain.Identity.MessageId.From(new Guid(value));

    private static ThreadId ThreadId(string value) =>
        Hive.Domain.Identity.ThreadId.From(new Guid(value));

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CapturingProjectionPublisher : IPositionProjectionPublisher
    {
        private readonly object _gate = new();
        private readonly List<PositionProjectionEvent> _events = new();

        public IReadOnlyList<PositionProjectionEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Publish(PositionProjectionEvent @event)
        {
            lock (_gate)
            {
                _events.Add(@event);
            }
        }

        public async Task<T> WaitForAsync<T>(Func<T, bool>? predicate = null)
            where T : PositionProjectionEvent
        {
            var deadline = DateTimeOffset.UtcNow.Add(Timeout());
            while (DateTimeOffset.UtcNow < deadline)
            {
                var match = Events
                    .OfType<T>()
                    .FirstOrDefault(candidate => predicate?.Invoke(candidate) ?? true);
                if (match is not null)
                {
                    return match;
                }

                await Task.Delay(25);
            }

            throw new TimeoutException($"Projection event {typeof(T).Name} was not published.");
        }
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
            Command<SeedEvent>(command =>
            {
                var replyTo = Sender;
                Persist(command.Event, _ => replyTo.Tell(EventSeeded.Instance));
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

    private sealed record SeedEvent(PositionEvent Event);

    private sealed record EventSeeded
    {
        public static EventSeeded Instance { get; } = new();
    }

    private sealed record ReadEvents
    {
        public static ReadEvents Instance { get; } = new();
    }
}
