using System.Collections.Immutable;
using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Akka.Persistence.Journal;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class PositionActorConfiguredOccupantBootstrapTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 13, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Empty_ai_journal_persists_occupant_before_dispatching_first_message()
    {
        var entity = Entity();
        var stamp = new PositionConfigurationStamp(1, "sha256:bootstrap-v1");
        var occupant = OccupantId.From("configured-ai:acme/bug-triage");
        var message = SampleMessage();
        var deliveries = new DeliveryCapture();
        var system = CreateActorSystem("position-bootstrap-empty-ai");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp, OccupantType.AiAgent, occupant),
                    new CapturingOccupantFactory(deliveries),
                    () => At)),
                "position");

            actor.Tell(new AcceptMessage(message));

            var delivered = await deliveries.NextAsync().WaitAsync(Timeout());
            await WaitForReadyAsync(actor);
            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system, entity);

            Assert.Equal(occupant, delivered.Occupant);
            Assert.Equal(OccupantType.AiAgent, delivered.Type);
            Assert.Same(message, delivered.Message);
            Assert.Single(events.OfType<OccupantChanged>());
            Assert.Equal(
                [
                    typeof(PositionConfigurationApplied),
                    typeof(OccupantChanged),
                    typeof(MessageReceived),
                    typeof(MessageDispatched),
                ],
                events.Select(@event => @event.GetType()).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Recovered_inbox_is_dispatched_once_after_configured_occupant_bootstrap()
    {
        var entity = Entity();
        var stamp = new PositionConfigurationStamp(2, "sha256:bootstrap-v2");
        var occupant = OccupantId.From("configured-ai:acme/bug-triage");
        var message = SampleMessage();
        var system = CreateActorSystem("position-bootstrap-recovered-inbox");
        var deliveries = new DeliveryCapture();

        try
        {
            await SeedEventsAsync(
                system,
                entity,
                new PositionConfigurationApplied(stamp, At),
                new MessageReceived(message, At));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp, OccupantType.AiAgent, occupant),
                    new CapturingOccupantFactory(deliveries),
                    () => At.AddMinutes(1))),
                "position");

            var delivered = await deliveries.NextAsync().WaitAsync(Timeout());
            await WaitForReadyAsync(actor);
            await Task.Delay(100);
            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system, entity);

            Assert.Same(message, delivered.Message);
            Assert.Equal(1, deliveries.Count);
            Assert.Single(events.OfType<MessageReceived>());
            Assert.Single(events.OfType<OccupantChanged>());
            Assert.Single(events.OfType<MessageDispatched>());
            Assert.Equal(
                [
                    typeof(PositionConfigurationApplied),
                    typeof(MessageReceived),
                    typeof(OccupantChanged),
                    typeof(MessageDispatched),
                ],
                events.Select(@event => @event.GetType()).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Restart_does_not_repeat_configured_occupant_materialization()
    {
        var entity = Entity();
        var stamp = new PositionConfigurationStamp(3, "sha256:bootstrap-v3");
        var occupant = OccupantId.From("configured-ai:acme/bug-triage");
        var system = CreateActorSystem("position-bootstrap-restart");

        try
        {
            var first = CreatePosition(system, "position-1", entity, stamp, occupant);
            await WaitForReadyAsync(first);
            await first.GracefulStop(Timeout());

            var second = CreatePosition(system, "position-2", entity, stamp, occupant);
            await WaitForReadyAsync(second);
            await second.GracefulStop(Timeout());

            var events = await ReadEventsAsync(system, entity);
            var changed = Assert.Single(events.OfType<OccupantChanged>());
            Assert.Equal(occupant, changed.Occupant);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Existing_divergent_occupant_is_preserved()
    {
        var entity = Entity();
        var stamp = new PositionConfigurationStamp(4, "sha256:bootstrap-v4");
        var existing = OccupantId.From("alice");
        var configured = OccupantId.From("configured-ai:acme/bug-triage");
        var system = CreateActorSystem("position-bootstrap-preserves-existing");

        try
        {
            await SeedEventsAsync(
                system,
                entity,
                new PositionConfigurationApplied(stamp, At),
                new OccupantChanged(existing, OccupantType.Human, At));

            var actor = CreatePosition(system, "position", entity, stamp, configured);
            await WaitForReadyAsync(actor);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());
            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system, entity);

            Assert.Equal(existing, state.Occupant);
            Assert.Equal(OccupantType.Human, state.OccupantType);
            var changed = Assert.Single(events.OfType<OccupantChanged>());
            Assert.Equal(existing, changed.Occupant);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Human_configuration_without_identity_remains_unoccupied()
    {
        var entity = Entity();
        var stamp = new PositionConfigurationStamp(5, "sha256:bootstrap-v5");
        var system = CreateActorSystem("position-bootstrap-human");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp, OccupantType.Human, null),
                    () => At)),
                "position");

            await WaitForReadyAsync(actor);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());
            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system, entity);

            Assert.Null(state.Occupant);
            Assert.Null(state.OccupantType);
            Assert.Empty(events.OfType<OccupantChanged>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Occupant_persistence_failure_stops_actor_without_opening_delivery_gate()
    {
        var entity = Entity();
        var stamp = new PositionConfigurationStamp(6, "sha256:bootstrap-v6");
        var occupant = OccupantId.From("configured-ai:acme/bug-triage");
        var deliveries = new DeliveryCapture();
        var terminated = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var system = CreateActorSystem("position-bootstrap-persist-failure", failingJournal: true);

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp, OccupantType.AiAgent, occupant),
                    new CapturingOccupantFactory(deliveries),
                    () => At)),
                "position");
            system.ActorOf(Props.Create(() => new TerminationWatcher(actor, terminated)));

            actor.Tell(new AcceptMessage(SampleMessage()));

            await terminated.Task.WaitAsync(Timeout());
            await Task.Delay(100);
            var events = await ReadEventsAsync(system, entity);

            Assert.Equal(0, deliveries.Count);
            Assert.Single(events.OfType<PositionConfigurationApplied>());
            Assert.Empty(events.OfType<OccupantChanged>());
            Assert.Empty(events.OfType<MessageReceived>());
            Assert.Empty(events.OfType<MessageDispatched>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static IActorRef CreatePosition(
        ActorSystem system,
        string name,
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        OccupantId configuredIdentity) =>
        system.ActorOf(
            Props.Create(() => new PositionActor(
                entity.Value,
                LoadedProvider(entity, stamp, OccupantType.AiAgent, configuredIdentity),
                () => At)),
            name);

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

            await Task.Delay(20);
        }

        throw new TimeoutException("PositionActor did not become ready.");
    }

    private static async Task SeedEventsAsync(
        ActorSystem system,
        PositionEntityId entity,
        params PositionEvent[] events)
    {
        var probe = system.ActorOf(
            Props.Create(() => new PersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
            $"seed-{Guid.NewGuid():N}");
        await probe.Ask<EventsSeeded>(new SeedEvents(events), Timeout());
        await probe.GracefulStop(Timeout());
    }

    private static async Task<IReadOnlyList<PositionEvent>> ReadEventsAsync(
        ActorSystem system,
        PositionEntityId entity)
    {
        var probe = system.ActorOf(
            Props.Create(() => new PersistenceProbe(PositionActor.PersistenceIdFor(entity.Value))),
            $"read-{Guid.NewGuid():N}");
        var events = await probe.Ask<IReadOnlyList<PositionEvent>>(ReadEvents.Instance, Timeout());
        await probe.GracefulStop(Timeout());
        return events;
    }

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        OccupantType type,
        OccupantId? configuredIdentity) =>
        new StaticConfigurationProvider(PositionRuntimeConfigurationLoadResult.Loaded(
            new PositionRuntimeConfiguration(
                stamp,
                entity.Organization,
                entity.Position,
                new PositionRuntimeDescriptor(
                    UnitId.From("engineering"),
                    PositionId.From("delivery-lead"),
                    "Bug triage",
                    "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(
                    type,
                    subscriptions: Array.Empty<SubscriptionConfiguration>(),
                    tools: Array.Empty<ToolConfiguration>(),
                    configuredIdentity: configuredIdentity),
                new PositionAuthorityRuntimeConfiguration(Array.Empty<string>()))));

    private static ActorSystem CreateActorSystem(string prefix, bool failingJournal = false)
    {
        var journal = failingJournal
            ? "akka.persistence.journal.bootstrap-failing"
            : "akka.persistence.journal.inmem";
        return ActorSystem.Create(
            $"{prefix}-{Guid.NewGuid():N}",
            ConfigurationFactory.ParseString($$"""
                akka.persistence.journal.plugin = "{{journal}}"
                akka.persistence.journal.bootstrap-failing {
                  class = "Hive.Tests.PositionActorConfiguredOccupantBootstrapTests+FailingOccupantChangeJournal, Hive.Tests"
                  plugin-dispatcher = "akka.actor.default-dispatcher"
                }
                akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
                akka.actor {
                  serializers {
                    hive-position-protocol = "Hive.Actors.Serialization.PositionProtocolJsonSerializer, Hive.Actors"
                  }
                  serialization-bindings {
                    "Hive.Domain.Positions.PositionEvent, Hive.Domain" = hive-position-protocol
                  }
                }
                """));
    }

    private static PositionEntityId Entity() =>
        PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));

    private static Memo SampleMessage() =>
        new(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000701")),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000701")),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Bootstrap delivery test.");

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(PositionRuntimeConfigurationLoadResult result)
        : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) => Task.FromResult(result);
    }

    private sealed class CapturingOccupantFactory(DeliveryCapture capture) : IPositionOccupantFactory
    {
        public Props Create(OccupantId occupant, OccupantType occupantType) =>
            Props.Create(() => new CapturingOccupantActor(occupant, occupantType, capture));
    }

    private sealed class CapturingOccupantActor : ReceiveActor
    {
        public CapturingOccupantActor(OccupantId occupant, OccupantType type, DeliveryCapture capture)
        {
            ReceiveAny(message => capture.Add(new Delivery(message, occupant, type)));
        }
    }

    private sealed class DeliveryCapture
    {
        private readonly object _gate = new();
        private readonly Queue<Delivery> _items = [];
        private readonly Queue<TaskCompletionSource<Delivery>> _waiters = [];
        private int _count;

        public int Count => Volatile.Read(ref _count);

        public Task<Delivery> NextAsync()
        {
            lock (_gate)
            {
                if (_items.TryDequeue(out var delivery))
                {
                    return Task.FromResult(delivery);
                }

                var waiter = new TaskCompletionSource<Delivery>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _waiters.Enqueue(waiter);
                return waiter.Task;
            }
        }

        public void Add(Delivery delivery)
        {
            Interlocked.Increment(ref _count);
            TaskCompletionSource<Delivery>? waiter;
            lock (_gate)
            {
                if (!_waiters.TryDequeue(out waiter))
                {
                    _items.Enqueue(delivery);
                    return;
                }
            }

            waiter.SetResult(delivery);
        }
    }

    private sealed class PersistenceProbe : ReceivePersistentActor
    {
        private readonly List<PositionEvent> _events = [];

        public PersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<PositionEvent>(_events.Add);
            RecoverAny(_ => { });
            Command<SeedEvents>(command =>
            {
                var replyTo = Sender;
                var remaining = command.Events.Count;
                PersistAll(command.Events, @event =>
                {
                    _events.Add(@event);
                    remaining--;
                    if (remaining == 0)
                    {
                        replyTo.Tell(EventsSeeded.Instance);
                    }
                });
            });
            Command<ReadEvents>(_ => Sender.Tell(_events.ToArray()));
        }

        public override string PersistenceId { get; }
    }

    private sealed class TerminationWatcher : ReceiveActor
    {
        public TerminationWatcher(IActorRef target, TaskCompletionSource<bool> terminated)
        {
            Context.Watch(target);
            Receive<Terminated>(_ => terminated.TrySetResult(true));
        }
    }

    public sealed class FailingOccupantChangeJournal : MemoryJournal
    {
        protected override Task<IImmutableList<Exception>> WriteMessagesAsync(
            IEnumerable<AtomicWrite> messages,
            CancellationToken cancellationToken)
        {
            var writes = messages.ToArray();
            if (writes
                .SelectMany(write => (IEnumerable<IPersistentRepresentation>)write.Payload)
                .Any(persistent => persistent.Payload is OccupantChanged))
            {
                return Task.FromException<IImmutableList<Exception>>(
                    new IOException("Forced OccupantChanged persistence failure."));
            }

            return base.WriteMessagesAsync(writes, cancellationToken);
        }
    }

    private sealed record Delivery(object Message, OccupantId Occupant, OccupantType Type);

    private sealed record SeedEvents(IReadOnlyList<PositionEvent> Events);

    private sealed record EventsSeeded
    {
        public static EventsSeeded Instance { get; } = new();
    }

    private sealed record ReadEvents
    {
        public static ReadEvents Instance { get; } = new();
    }
}
