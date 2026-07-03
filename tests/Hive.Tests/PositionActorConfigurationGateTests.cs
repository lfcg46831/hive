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
/// Verifies US-F0-06-T08d: the PositionActor loads runtime configuration after recovery, applies
/// the recoverable configuration stamp, and blocks business side effects until the entity is ready.
/// </summary>
public sealed class PositionActorConfigurationGateTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Recovery_without_persisted_stamp_applies_current_configuration_once_before_ready()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var system = CreateActorSystem("position-config-no-stamp");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    () => At)),
                "position-config-no-stamp-actor");

            var status = await WaitForStatusAsync(actor, PositionOperationalState.Ready);

            Assert.Equal(stamp, status.LastConfigurationStamp);

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            var applied = Assert.Single(events.OfType<PositionConfigurationApplied>());
            Assert.Equal(stamp, applied.Stamp);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Already_applied_stamp_enters_ready_without_persisting_duplicate_configuration_event()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(2, "sha256:v2");
        var system = CreateActorSystem("position-config-already-applied");

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    () => At.AddMinutes(1))),
                "position-config-already-applied-actor");

            var status = await WaitForStatusAsync(actor, PositionOperationalState.Ready);

            Assert.Equal(stamp, status.LastConfigurationStamp);

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(events.OfType<PositionConfigurationApplied>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Newer_configuration_version_is_applied_once_after_recovery()
    {
        var entity = EntityId("acme", "bug-triage");
        var recovered = new PositionConfigurationStamp(2, "sha256:v2");
        var current = new PositionConfigurationStamp(3, "sha256:v3");
        var system = CreateActorSystem("position-config-newer-version");

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: recovered));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, current),
                    () => At.AddMinutes(1))),
                "position-config-newer-version-actor");

            var status = await WaitForStatusAsync(actor, PositionOperationalState.Ready);

            Assert.Equal(current, status.LastConfigurationStamp);

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            var applied = Assert.Single(events.OfType<PositionConfigurationApplied>());
            Assert.Equal(current, applied.Stamp);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Theory]
    [InlineData(2, "sha256:changed", PositionConfigurationBlockReason.FingerprintChangedForVersion)]
    [InlineData(1, "sha256:v1", PositionConfigurationBlockReason.RecoveredVersionNewer)]
    public async Task Incompatible_loaded_configuration_blocks_without_business_side_effects(
        long currentVersion,
        string currentFingerprint,
        PositionConfigurationBlockReason expectedReason)
    {
        var entity = EntityId("acme", "bug-triage");
        var recovered = new PositionConfigurationStamp(2, "sha256:v2");
        var current = new PositionConfigurationStamp(currentVersion, currentFingerprint);
        var system = CreateActorSystem($"position-config-blocked-{currentVersion}");

        try
        {
            await SeedSnapshotAsync(system, entity, new PositionSnapshot(At, lastConfigurationStamp: recovered));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, current),
                    () => At.AddMinutes(1))),
                $"position-config-blocked-{currentVersion}-actor");

            var status = await WaitForStatusAsync(actor, PositionOperationalState.ConfigurationBlocked);

            Assert.Equal(expectedReason, status.BlockReason);

            actor.Tell(new UpdateShortMemory("blocked", "must-not-persist"));
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.False(state.ShortMemory.ContainsKey("blocked"));

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(events.OfType<PositionConfigurationApplied>());
            Assert.Empty(events.OfType<ShortMemoryUpdated>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Missing_configuration_blocks_without_business_side_effects()
    {
        var entity = EntityId("acme", "bug-triage");
        var provider = new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Missing("position not found"));
        var system = CreateActorSystem("position-config-missing");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At)),
                "position-config-missing-actor");

            var status = await WaitForStatusAsync(actor, PositionOperationalState.ConfigurationBlocked);

            Assert.Equal(PositionConfigurationBlockReason.ConfigurationMissing, status.BlockReason);

            actor.Tell(new UpdateShortMemory("blocked", "must-not-persist"));
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.False(state.ShortMemory.ContainsKey("blocked"));

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            Assert.Empty(events.OfType<PositionConfigurationApplied>());
            Assert.Empty(events.OfType<ShortMemoryUpdated>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Business_commands_received_while_loading_are_stashed_until_ready()
    {
        var entity = EntityId("acme", "bug-triage");
        var provider = new DeferredConfigurationProvider();
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var system = CreateActorSystem("position-config-loading-stash");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(entity.Value, provider, () => At)),
                "position-config-loading-stash-actor");

            await WaitForStatusAsync(actor, PositionOperationalState.LoadingConfiguration);

            actor.Tell(new UpdateShortMemory("pending", "applied-after-ready"));
            var beforeReady = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.False(beforeReady.ShortMemory.ContainsKey("pending"));

            provider.Complete(PositionRuntimeConfigurationLoadResult.Loaded(RuntimeConfiguration(entity, stamp)));

            var afterReady = await WaitForShortMemoryAsync(actor, "pending");

            Assert.Equal("applied-after-ready", afterReady.ShortMemory["pending"]);
            Assert.Equal(stamp, afterReady.LastConfigurationStamp);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Technical_configuration_failure_is_escalated_to_supervision()
    {
        var entity = EntityId("acme", "bug-triage");
        var failure = new TimeoutException("registry timeout");
        var stopped = new TaskCompletionSource<Exception>(TaskCreationOptions.RunContinuationsAsynchronously);
        var provider = new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.TechnicalFailure(failure));
        var system = CreateActorSystem("position-config-technical-failure");

        try
        {
            system.ActorOf(
                Props.Create(() => new StopOnFailureSupervisor(entity.Value, provider, stopped)),
                "position-config-technical-failure-supervisor");

            var observed = await stopped.Task.WaitAsync(Timeout());

            Assert.Same(failure, observed.InnerException);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Replayed_configuration_stamp_and_snapshot_stamp_produce_equivalent_ready_state()
    {
        var replayEntity = EntityId("acme", "bug-triage");
        var snapshotEntity = EntityId("acme", "delivery-lead");
        var replayStamp = new PositionConfigurationStamp(4, "sha256:v4");
        var snapshotStamp = new PositionConfigurationStamp(4, "sha256:v4");
        var system = CreateActorSystem("position-config-replay-vs-snapshot");

        try
        {
            await SeedEventAsync(
                system,
                replayEntity,
                new PositionConfigurationApplied(replayStamp, At));
            await SeedSnapshotAsync(
                system,
                snapshotEntity,
                new PositionSnapshot(At, lastConfigurationStamp: snapshotStamp));

            var replayActor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    replayEntity.Value,
                    LoadedProvider(replayEntity, replayStamp),
                    () => At.AddMinutes(1))),
                "position-config-replay-actor");
            var snapshotActor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    snapshotEntity.Value,
                    LoadedProvider(snapshotEntity, snapshotStamp),
                    () => At.AddMinutes(1))),
                "position-config-snapshot-actor");

            var replayStatus = await WaitForStatusAsync(replayActor, PositionOperationalState.Ready);
            var snapshotStatus = await WaitForStatusAsync(snapshotActor, PositionOperationalState.Ready);
            var replayState = await replayActor.Ask<PositionState>(GetPositionState.Instance, Timeout());
            var snapshotState = await snapshotActor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(PositionOperationalState.Ready, replayStatus.OperationalState);
            Assert.Equal(PositionOperationalState.Ready, snapshotStatus.OperationalState);
            Assert.Equal(replayStamp, replayState.LastConfigurationStamp);
            Assert.Equal(snapshotStamp, snapshotState.LastConfigurationStamp);
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

    private static async Task<PositionRuntimeStatus> WaitForStatusAsync(
        IActorRef actor,
        PositionOperationalState expected)
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

            if (latest.OperationalState == expected)
            {
                return latest;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PositionActor did not reach {expected}. Last observed state was {latest?.OperationalState}.");
    }

    private static async Task<PositionState> WaitForShortMemoryAsync(IActorRef actor, string key)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        PositionState? latest = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            latest = await actor.Ask<PositionState>(GetPositionState.Instance, TimeSpan.FromSeconds(1));
            if (latest.ShortMemory.ContainsKey(key))
            {
                return latest;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException(
            $"PositionActor did not apply short-memory key '{key}'. Latest count was {latest?.ShortMemory.Count}.");
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

    private sealed class DeferredConfigurationProvider : IPositionConfigurationProvider
    {
        private readonly TaskCompletionSource<PositionRuntimeConfigurationLoadResult> _completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            _completion.Task;

        public void Complete(PositionRuntimeConfigurationLoadResult result) =>
            _completion.SetResult(result);
    }

    private sealed class StopOnFailureSupervisor : ReceiveActor
    {
        private readonly TaskCompletionSource<Exception> _stopped;

        public StopOnFailureSupervisor(
            string entityId,
            IPositionConfigurationProvider provider,
            TaskCompletionSource<Exception> stopped)
        {
            _stopped = stopped;

            var child = Context.ActorOf(
                Props.Create(() => new PositionActor(entityId, provider, () => At)),
                "position");

            ReceiveAny(message => child.Forward(message));
        }

        protected override SupervisorStrategy SupervisorStrategy() =>
            new OneForOneStrategy(exception =>
            {
                _stopped.TrySetResult(exception);
                return Akka.Actor.Directive.Stop;
            });
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
