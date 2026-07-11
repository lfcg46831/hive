using Akka.Actor;
using Akka.Cluster.Sharding;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T11a at the PositionActor boundary: RequestPassivation is admitted only when
/// the recovered state and runtime configuration satisfy the safe-passivation guard rails.
/// </summary>
public sealed class PositionActorPassivationTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Idle_ready_position_persists_passivation_fact()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var system = CreateActorSystem("position-passivation-allowed");

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: stamp));
            var provider = LoadedProvider(
                entity,
                stamp,
                Array.Empty<PositionScheduleRuntimeConfiguration>(),
                Array.Empty<SubscriptionConfiguration>());

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    () => At.AddMinutes(1))),
                "position-passivation-allowed-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new RequestPassivation("idle"));

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            var passivated = Assert.Single(events.OfType<PositionPassivated>());
            Assert.Equal("idle", passivated.Reason);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Pending_delivery_prevents_passivation_fact_from_being_persisted()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var message = SampleMessage();
        var system = CreateActorSystem("position-passivation-pending-delivery");

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
            var provider = LoadedProvider(
                entity,
                stamp,
                Array.Empty<PositionScheduleRuntimeConfiguration>(),
                Array.Empty<SubscriptionConfiguration>());

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    () => At.AddMinutes(1))),
                "position-passivation-pending-delivery-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new RequestPassivation("idle"));

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(events.OfType<PositionPassivated>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Critical_open_task_prevents_passivation_fact_from_being_persisted()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var criticalTask = new PersistedTask(
            PositionTaskId.New(),
            ThreadId.New(),
            "Resolve production incident",
            Priority.Critical,
            At);
        var system = CreateActorSystem("position-passivation-critical-task");

        try
        {
            await SeedSnapshotAsync(
                system,
                entity,
                new PositionSnapshot(
                    At,
                    openTasks: new[] { criticalTask },
                    lastConfigurationStamp: stamp));
            var provider = LoadedProvider(
                entity,
                stamp,
                Array.Empty<PositionScheduleRuntimeConfiguration>(),
                Array.Empty<SubscriptionConfiguration>());

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    () => At.AddMinutes(1))),
                "position-passivation-critical-task-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new RequestPassivation("idle"));

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(events.OfType<PositionPassivated>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Active_schedule_or_subscription_prevents_passivation_fact_from_being_persisted()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var system = CreateActorSystem("position-passivation-active-triggers");

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: stamp));
            var provider = LoadedProvider(
                entity,
                stamp,
                new[] { new PositionScheduleRuntimeConfiguration("daily-pulse", "0 8 * * *", "Send pulse") },
                new[] { new SubscriptionConfiguration("deadline.near", "PT2H") });

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    provider,
                    () => At.AddMinutes(1))),
                "position-passivation-active-triggers-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new RequestPassivation("idle"));

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(events.OfType<PositionPassivated>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Allowed_passivation_requests_shard_passivation_after_persisting_fact()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var system = CreateActorSystem("position-passivation-shard-stop");
        var passivationObserved = new TaskCompletionSource<Passivate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<Terminated>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: stamp));
            var provider = LoadedProvider(
                entity,
                stamp,
                Array.Empty<PositionScheduleRuntimeConfiguration>(),
                Array.Empty<SubscriptionConfiguration>());

            var actor = system.ActorOf(
                Props.Create(() => new PassivationShardHarness(
                    entity.Value,
                    provider,
                    passivationObserved,
                    stopped,
                    () => At.AddMinutes(1))),
                "position-passivation-shard-stop-harness");

            await WaitForReadyAsync(actor);
            actor.Tell(new RequestPassivation("idle"));

            var passivate = await passivationObserved.Task.WaitAsync(Timeout());
            Assert.IsType<PositionPassivationStop>(passivate.StopMessage);

            await stopped.Task.WaitAsync(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            var passivated = Assert.Single(events.OfType<PositionPassivated>());
            Assert.Equal("idle", passivated.Reason);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task In_flight_business_command_drains_before_passivation_stop()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var system = CreateActorSystem("position-passivation-drain");
        var passivationObserved = new TaskCompletionSource<Passivate>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource<Terminated>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: stamp));
            var provider = LoadedProvider(
                entity,
                stamp,
                Array.Empty<PositionScheduleRuntimeConfiguration>(),
                Array.Empty<SubscriptionConfiguration>());

            var actor = system.ActorOf(
                Props.Create(() => new PassivationShardHarness(
                    entity.Value,
                    provider,
                    passivationObserved,
                    stopped,
                    () => At.AddMinutes(1))),
                "position-passivation-drain-harness");

            await WaitForReadyAsync(actor);
            actor.Tell(new RequestPassivation("idle"));
            actor.Tell(new UpdateShortMemory("late", "drained-before-stop"));

            await passivationObserved.Task.WaitAsync(Timeout());
            await stopped.Task.WaitAsync(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);

            Assert.Contains(events, @event => @event is PositionPassivated);
            var shortMemory = Assert.Single(events.OfType<ShortMemoryUpdated>());
            Assert.Equal("late", shortMemory.Key);
            Assert.Equal("drained-before-stop", shortMemory.Value);
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
                // Recovery temporarily suspends command handling. Keep polling until the
                // overall readiness deadline instead of failing on the first short Ask timeout.
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

    private static Memo SampleMessage() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            ThreadId.New(),
            Priority.Normal,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            body: "Customer reported a regression.");

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        IEnumerable<PositionScheduleRuntimeConfiguration> schedules,
        IEnumerable<SubscriptionConfiguration> subscriptions) =>
        new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Loaded(RuntimeConfiguration(
                entity,
                stamp,
                schedules,
                subscriptions)));

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        IEnumerable<PositionScheduleRuntimeConfiguration>? schedules,
        IEnumerable<SubscriptionConfiguration>? subscriptions) =>
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
                subscriptions: subscriptions ?? Array.Empty<SubscriptionConfiguration>(),
                tools: Array.Empty<ToolConfiguration>()),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: Array.Empty<string>()),
            schedules ?? Array.Empty<PositionScheduleRuntimeConfiguration>());

    private static PositionEntityId EntityId(string organization, string position) =>
        PositionEntityId.From(OrganizationId.From(organization), PositionId.From(position));

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class PassivationShardHarness : ReceiveActor
    {
        private readonly IActorRef _position;
        private readonly TaskCompletionSource<Passivate> _passivationObserved;
        private readonly TaskCompletionSource<Terminated> _stopped;

        public PassivationShardHarness(
            string entityId,
            IPositionConfigurationProvider provider,
            TaskCompletionSource<Passivate> passivationObserved,
            TaskCompletionSource<Terminated> stopped,
            Func<DateTimeOffset> clock)
        {
            _passivationObserved = passivationObserved;
            _stopped = stopped;
            _position = Context.ActorOf(
                Props.Create(() => new PositionActor(entityId, provider, clock)),
                "position");
            Context.Watch(_position);

            Receive<Passivate>(passivate =>
            {
                _passivationObserved.TrySetResult(passivate);
                _position.Tell(passivate.StopMessage);
            });
            Receive<Terminated>(terminated =>
            {
                if (terminated.ActorRef.Equals(_position))
                {
                    _stopped.TrySetResult(terminated);
                    Context.Stop(Self);
                }
            });
            ReceiveAny(message => _position.Forward(message));
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
