using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T09: accepted messages are dispatched to the current occupant only after the
/// PositionActor is ready, while the position keeps inbox/history as durable state.
/// </summary>
public sealed class PositionActorDispatchTests
{
    private static readonly DateTimeOffset At = new(2026, 6, 27, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Accepted_message_for_ready_position_is_forwarded_to_current_occupant_and_recorded_as_dispatched()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(1, "sha256:v1");
        var occupant = OccupantId.From("agent-7");
        var message = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000301"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000301"));
        var system = CreateActorSystem("position-dispatch-ready");
        var capture = new TaskCompletionSource<DispatchedToOccupant>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await SeedSnapshotAsync(
                system,
                entity,
                new PositionSnapshot(
                    At,
                    occupant,
                    OccupantType.AiAgent,
                    lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    new CapturingOccupantFactory(capture),
                    () => At.AddMinutes(1))),
                "position-dispatch-ready-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(message));

            var dispatched = await capture.Task.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(message, dispatched.Message);
            Assert.Equal(occupant, dispatched.Occupant);
            Assert.Equal(OccupantType.AiAgent, dispatched.OccupantType);
            Assert.Empty(state.Inbox);
            Assert.Equal(new[] { message.Id }, state.RecentHistory);
            Assert.Contains(message.Id, state.ProcessedMessages);

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            var received = Assert.Single(events.OfType<MessageReceived>());
            var dispatchEvent = Assert.Single(events.OfType<MessageDispatched>());

            Assert.Equal(message.Id, received.Message.Id);
            Assert.Equal(message.Id, dispatchEvent.Message);
            Assert.Equal(message.Thread, dispatchEvent.Thread);
            Assert.Equal(occupant, dispatchEvent.Occupant);
            Assert.Equal(OccupantType.AiAgent, dispatchEvent.OccupantType);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Accepted_directive_for_ready_ai_position_is_forwarded_as_processing_request_with_limits_and_context()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(2, "sha256:v2");
        var occupant = OccupantId.From("agent-7");
        var previousMessage = MessageId("aaaaaaaa-0000-0000-0000-000000000399");
        var directive = SampleDirective(
            MessageId("aaaaaaaa-0000-0000-0000-000000000401"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000401"));
        var task = new PersistedTask(
            PositionTaskId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000000401")),
            directive.Thread,
            "Investigate regression",
            Priority.High,
            At,
            causedBy: previousMessage);
        var aiGateway = new AiPositionRuntimeConfiguration(
            new AiProviderMetadata("stub", "triage"),
            new AiModelParameters(maxOutputTokens: 700),
            timeout: TimeSpan.FromSeconds(15),
            costLimits: new AiCostLimits(maxCallsPerHour: 9));
        var system = CreateActorSystem("position-dispatch-ai-directive");
        var capture = new TaskCompletionSource<DispatchedToOccupant>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await SeedSnapshotAsync(
                system,
                entity,
                new PositionSnapshot(
                    At,
                    occupant,
                    OccupantType.AiAgent,
                    openTasks: [task],
                    shortMemory: new Dictionary<string, string>
                    {
                        ["recent-customer"] = "contoso",
                    },
                    recentHistory: [previousMessage],
                    lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp, aiGateway),
                    new CapturingOccupantFactory(capture),
                    () => At.AddMinutes(1))),
                "position-dispatch-ai-directive-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(directive));

            var dispatched = await capture.Task.WaitAsync(Timeout());
            var request = Assert.IsType<AiDirectiveProcessingRequest>(dispatched.Message);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(occupant, dispatched.Occupant);
            Assert.Equal(OccupantType.AiAgent, dispatched.OccupantType);
            Assert.Equal(entity.Organization, request.OrganizationId);
            Assert.Equal(entity.Position, request.PositionId);
            Assert.Equal(occupant, request.Occupant);
            Assert.Same(directive, request.Directive);
            Assert.Equal(directive.Thread, request.ThreadId);
            Assert.Equal(directive.DirectiveId, request.DirectiveId);
            Assert.Equal(directive.Id, request.MessageId);
            Assert.Equal(
                "directive:cccccccc000000000000000000000401:message:aaaaaaaa000000000000000000000401",
                request.CorrelationId);
            Assert.Equal(TimeSpan.FromSeconds(15), request.Limits.Timeout);
            Assert.Equal(700, request.Limits.MaxOutputTokens);
            Assert.Null(request.Limits.MaxIterations);
            Assert.Equal(9, request.Limits.CostLimits!.MaxCallsPerHour);
            Assert.Equal(stamp, request.PersistedContext.LastConfigurationStamp);
            Assert.Equal("contoso", request.PersistedContext.ShortMemory["recent-customer"]);
            Assert.Equal(new[] { task }, request.PersistedContext.OpenTasks);
            Assert.Equal(new[] { previousMessage, directive.Id }, request.PersistedContext.RecentHistory);
            Assert.Empty(state.Inbox);
            Assert.Equal(new[] { previousMessage, directive.Id }, state.RecentHistory);
            Assert.Contains(directive.Id, state.ProcessedMessages);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Accepted_directive_for_ready_human_position_is_forwarded_as_original_org_message()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(3, "sha256:v3");
        var occupant = OccupantId.From("alice");
        var directive = SampleDirective(
            MessageId("aaaaaaaa-0000-0000-0000-000000000501"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000501"));
        var system = CreateActorSystem("position-dispatch-human-directive");
        var capture = new TaskCompletionSource<DispatchedToOccupant>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await SeedSnapshotAsync(
                system,
                entity,
                new PositionSnapshot(
                    At,
                    occupant,
                    OccupantType.Human,
                    lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp, OccupantType.Human),
                    new CapturingOccupantFactory(capture),
                    () => At.AddMinutes(1))),
                "position-dispatch-human-directive-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(directive));

            var dispatched = await capture.Task.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Same(directive, dispatched.Message);
            Assert.IsNotType<AiDirectiveProcessingRequest>(dispatched.Message);
            Assert.Equal(occupant, dispatched.Occupant);
            Assert.Equal(OccupantType.Human, dispatched.OccupantType);
            Assert.Empty(state.Inbox);
            Assert.Equal(new[] { directive.Id }, state.RecentHistory);
            Assert.Contains(directive.Id, state.ProcessedMessages);

            await actor.GracefulStop(Timeout());
            var events = await ReadPersistedEventsAsync(system, entity);
            var dispatchEvent = Assert.Single(events.OfType<MessageDispatched>());

            Assert.Equal(directive.Id, dispatchEvent.Message);
            Assert.Equal(directive.Thread, dispatchEvent.Thread);
            Assert.Equal(occupant, dispatchEvent.Occupant);
            Assert.Equal(OccupantType.Human, dispatchEvent.OccupantType);
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

    private static OrgDirective SampleDirective(MessageId id, ThreadId thread) =>
        new(
            id,
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000401")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reported a regression.");

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp) =>
        LoadedProvider(entity, stamp, aiGateway: null);

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        OccupantType occupantType) =>
        LoadedProvider(entity, stamp, aiGateway: null, occupantType);

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        AiPositionRuntimeConfiguration? aiGateway) =>
        LoadedProvider(entity, stamp, aiGateway, OccupantType.AiAgent);

    private static IPositionConfigurationProvider LoadedProvider(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        AiPositionRuntimeConfiguration? aiGateway,
        OccupantType occupantType) =>
        new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Loaded(RuntimeConfiguration(
                entity,
                stamp,
                aiGateway,
                occupantType)));

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        AiPositionRuntimeConfiguration? aiGateway,
        OccupantType occupantType) =>
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
                occupantType,
                identityPromptRef: "engineer-v1",
                ai: null,
                workingHours: null,
                subscriptions: Array.Empty<SubscriptionConfiguration>(),
                tools: Array.Empty<ToolConfiguration>(),
                aiGateway: aiGateway),
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

    private sealed class CapturingOccupantFactory(
        TaskCompletionSource<DispatchedToOccupant> capture) : IPositionOccupantFactory
    {
        public Props Create(OccupantId occupant, OccupantType occupantType) =>
            Props.Create(() => new CapturingOccupantActor(occupant, occupantType, capture));
    }

    private sealed class CapturingOccupantActor : ReceiveActor
    {
        public CapturingOccupantActor(
            OccupantId occupant,
            OccupantType occupantType,
            TaskCompletionSource<DispatchedToOccupant> capture)
        {
            ReceiveAny(message =>
                capture.TrySetResult(new DispatchedToOccupant(message, occupant, occupantType)));
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

    private sealed record DispatchedToOccupant(
        object Message,
        OccupantId Occupant,
        OccupantType OccupantType);

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
