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
            Assert.Equal(new[] { message.Id }, state.Inbox.Select(inboxMessage => inboxMessage.Id));
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
            causedBy: directive.Id);
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
            Assert.Equal(AiDirectiveTaskStateStatus.Open, request.TaskState.Status);
            Assert.Same(task, request.TaskState.Task);
            Assert.Equal(new[] { task }, request.TaskState.Matches);
            Assert.Equal(new[] { directive.Id }, state.Inbox.Select(inboxMessage => inboxMessage.Id));
            Assert.Equal(new[] { previousMessage, directive.Id }, state.RecentHistory);
            Assert.Contains(directive.Id, state.ProcessedMessages);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Default_ai_occupant_completes_generic_message_delivery()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(21, "sha256:v21");
        var occupant = OccupantId.From("agent-7");
        var message = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000421"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000421"));
        var system = CreateActorSystem("position-dispatch-generic-completion");

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
                    new PositionOccupantFactory(),
                    () => At.AddMinutes(1))),
                "position-dispatch-generic-completion-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(message));

            var state = await WaitForStateAsync(actor, current => current.Inbox.IsEmpty);
            var events = await ReadPersistedEventsAsync(system, entity);
            var completed = Assert.Single(events.OfType<MessageProcessingCompleted>());

            Assert.Empty(state.Inbox);
            Assert.Equal(message.Id, completed.Message);
            Assert.Equal(message.Thread, completed.Thread);
            Assert.Equal(MessageProcessingCompletionStatus.Completed, completed.Status);
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
            Assert.Equal(new[] { directive.Id }, state.Inbox.Select(inboxMessage => inboxMessage.Id));
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

    [Fact]
    public async Task Recovered_dispatched_message_without_completion_is_redelivered_without_new_dispatch_event()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(31, "sha256:v31");
        var occupant = OccupantId.From("agent-7");
        var directive = SampleDirective(
            MessageId("aaaaaaaa-0000-0000-0000-000000000531"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000531"));
        var system = CreateActorSystem("position-dispatch-recovered-inflight");
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
                    inbox: [directive],
                    recentHistory: [directive.Id],
                    processedMessages: [directive.Id],
                    lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    new CapturingOccupantFactory(capture),
                    () => At.AddMinutes(1))),
                "position-dispatch-recovered-inflight-actor");

            await WaitForReadyAsync(actor);

            var dispatched = await capture.Task.WaitAsync(Timeout());
            var request = Assert.IsType<AiDirectiveProcessingRequest>(dispatched.Message);
            var events = await ReadPersistedEventsAsync(system, entity);

            Assert.Equal(directive.Id, request.MessageId);
            Assert.Empty(events.OfType<MessageDispatched>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Occupant_processing_completion_is_persisted_and_removes_message_from_recoverable_inbox()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(32, "sha256:v32");
        var occupant = OccupantId.From("agent-7");
        var directive = SampleDirective(
            MessageId("aaaaaaaa-0000-0000-0000-000000000532"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000532"));
        var system = CreateActorSystem("position-dispatch-completion");
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
                "position-dispatch-completion-actor");

            await WaitForReadyAsync(actor);
            actor.Tell(new AcceptMessage(directive));

            var dispatched = await capture.Task.WaitAsync(Timeout());
            var request = Assert.IsType<AiDirectiveProcessingRequest>(dispatched.Message);

            actor.Tell(new PositionOccupantProcessingCompleted(
                request.CorrelationId,
                request.MessageId,
                request.ThreadId,
                request.DirectiveId,
                PositionOccupantProcessingStatus.Completed));

            var state = await WaitForStateAsync(actor, current => current.Inbox.IsEmpty);
            var events = await ReadPersistedEventsAsync(system, entity);
            var completed = Assert.Single(events.OfType<MessageProcessingCompleted>());

            Assert.Empty(state.Inbox);
            Assert.Equal(directive.Id, completed.Message);
            Assert.Equal(directive.Thread, completed.Thread);
            Assert.Equal(MessageProcessingCompletionStatus.Completed, completed.Status);
            Assert.Equal(request.CorrelationId, completed.CorrelationId);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Occupant_change_stops_previous_managed_child_before_dispatching_to_new_child()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(4, "sha256:v4");
        var firstOccupant = OccupantId.From("agent-7");
        var nextOccupant = OccupantId.From("agent-8");
        var firstMessage = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000601"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000601"));
        var secondMessage = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000602"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000602"));
        var system = CreateActorSystem("position-dispatch-managed-child");
        var factory = new TrackingOccupantFactory();

        try
        {
            await SeedSnapshotAsync(
                system,
                entity,
                new PositionSnapshot(
                    At,
                    firstOccupant,
                    OccupantType.AiAgent,
                    lastConfigurationStamp: stamp));

            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity, stamp),
                    factory,
                    () => At.AddMinutes(1))),
                "position-dispatch-managed-child-actor");

            await WaitForReadyAsync(actor);

            var firstDeliveryTask = factory.NextDeliveryAsync();
            actor.Tell(new AcceptMessage(firstMessage));
            var firstDelivery = await firstDeliveryTask.WaitAsync(Timeout());

            Assert.Equal(firstMessage, firstDelivery.Message);
            Assert.Equal(firstOccupant, firstDelivery.Occupant);
            Assert.Equal(OccupantType.AiAgent, firstDelivery.OccupantType);

            actor.Tell(new ChangeOccupant(nextOccupant, OccupantType.AiAgent));
            var stopped = await factory
                .StopOfAsync(firstDelivery.Child)
                .WaitAsync(Timeout());

            Assert.Equal(firstOccupant, stopped.Occupant);
            Assert.Equal(OccupantType.AiAgent, stopped.OccupantType);

            var redelivery = await factory.NextDeliveryAsync().WaitAsync(Timeout());
            Assert.Equal(firstMessage, redelivery.Message);
            Assert.Equal(nextOccupant, redelivery.Occupant);

            var secondDeliveryTask = factory.NextDeliveryAsync();
            actor.Tell(new AcceptMessage(secondMessage));
            var secondDelivery = await secondDeliveryTask.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(secondMessage, secondDelivery.Message);
            Assert.Equal(nextOccupant, secondDelivery.Occupant);
            Assert.Equal(OccupantType.AiAgent, secondDelivery.OccupantType);
            Assert.NotEqual(firstDelivery.Child, secondDelivery.Child);
            Assert.Equal(
                new[] { firstMessage.Id, secondMessage.Id },
                state.Inbox.Select(inboxMessage => inboxMessage.Id));
            Assert.Equal(nextOccupant, state.Occupant);
            Assert.Equal(OccupantType.AiAgent, state.OccupantType);
            Assert.Contains(firstMessage.Id, state.ProcessedMessages);
            Assert.Contains(secondMessage.Id, state.ProcessedMessages);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Repeated_deliveries_to_same_occupant_reuse_managed_child()
    {
        var entity = EntityId("acme", "bug-triage");
        var stamp = new PositionConfigurationStamp(5, "sha256:v5");
        var occupant = OccupantId.From("agent-7");
        var firstMessage = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000701"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000701"));
        var secondMessage = SampleMessage(
            MessageId("aaaaaaaa-0000-0000-0000-000000000702"),
            ThreadId("bbbbbbbb-0000-0000-0000-000000000702"));
        var system = CreateActorSystem("position-dispatch-reuse-managed-child");
        var factory = new TrackingOccupantFactory();

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
                    factory,
                    () => At.AddMinutes(1))),
                "position-dispatch-reuse-managed-child-actor");

            await WaitForReadyAsync(actor);

            var firstDeliveryTask = factory.NextDeliveryAsync();
            actor.Tell(new AcceptMessage(firstMessage));
            var firstDelivery = await firstDeliveryTask.WaitAsync(Timeout());

            var secondDeliveryTask = factory.NextDeliveryAsync();
            actor.Tell(new AcceptMessage(secondMessage));
            var secondDelivery = await secondDeliveryTask.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(occupant, firstDelivery.Occupant);
            Assert.Equal(occupant, secondDelivery.Occupant);
            Assert.Equal(firstDelivery.Child, secondDelivery.Child);
            Assert.Equal(
                new[] { firstMessage.Id, secondMessage.Id },
                state.Inbox.Select(inboxMessage => inboxMessage.Id));
            Assert.Equal(new[] { firstMessage.Id, secondMessage.Id }, state.RecentHistory);
            Assert.Contains(firstMessage.Id, state.ProcessedMessages);
            Assert.Contains(secondMessage.Id, state.ProcessedMessages);
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
            PositionRuntimeStatus status;
            try
            {
                status = await actor.Ask<PositionRuntimeStatus>(
                    GetPositionRuntimeStatus.Instance,
                    TimeSpan.FromSeconds(1));
            }
            catch (AskTimeoutException) when (DateTimeOffset.UtcNow < deadline)
            {
                continue;
            }

            if (status.OperationalState == PositionOperationalState.Ready)
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("PositionActor did not reach Ready.");
    }

    private static async Task<PositionState> WaitForStateAsync(
        IActorRef actor,
        Func<PositionState, bool> predicate)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(
                GetPositionState.Instance,
                TimeSpan.FromSeconds(1));
            if (predicate(state))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("PositionActor state did not reach the expected condition.");
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
                canDecide: Array.Empty<string>()));

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

    private sealed class TrackingOccupantFactory : IPositionOccupantFactory
    {
        private readonly object _gate = new();
        private readonly Queue<TrackedDelivery> _deliveries = new();
        private readonly Queue<TaskCompletionSource<TrackedDelivery>> _deliveryWaiters = new();
        private readonly Dictionary<IActorRef, TrackedChildStopped> _stopped = new();
        private readonly Dictionary<IActorRef, TaskCompletionSource<TrackedChildStopped>> _stopWaiters = new();

        public Props Create(OccupantId occupant, OccupantType occupantType) =>
            Props.Create(() => new TrackingOccupantActor(occupant, occupantType, this));

        public Task<TrackedDelivery> NextDeliveryAsync()
        {
            lock (_gate)
            {
                if (_deliveries.TryDequeue(out var delivery))
                {
                    return Task.FromResult(delivery);
                }

                var waiter = new TaskCompletionSource<TrackedDelivery>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _deliveryWaiters.Enqueue(waiter);
                return waiter.Task;
            }
        }

        public Task<TrackedChildStopped> StopOfAsync(IActorRef child)
        {
            lock (_gate)
            {
                if (_stopped.TryGetValue(child, out var stopped))
                {
                    return Task.FromResult(stopped);
                }

                var waiter = new TaskCompletionSource<TrackedChildStopped>(
                    TaskCreationOptions.RunContinuationsAsynchronously);
                _stopWaiters[child] = waiter;
                return waiter.Task;
            }
        }

        internal void Delivered(TrackedDelivery delivery)
        {
            TaskCompletionSource<TrackedDelivery>? waiter = null;
            lock (_gate)
            {
                if (_deliveryWaiters.TryDequeue(out waiter))
                {
                }
                else
                {
                    _deliveries.Enqueue(delivery);
                    return;
                }
            }

            waiter.SetResult(delivery);
        }

        internal void Stopped(TrackedChildStopped stopped)
        {
            TaskCompletionSource<TrackedChildStopped>? waiter = null;
            lock (_gate)
            {
                _stopped[stopped.Child] = stopped;
                if (_stopWaiters.Remove(stopped.Child, out waiter))
                {
                }
                else
                {
                    return;
                }
            }

            waiter.SetResult(stopped);
        }
    }

    private sealed class TrackingOccupantActor : ReceiveActor
    {
        private readonly OccupantId _occupant;
        private readonly OccupantType _occupantType;
        private readonly TrackingOccupantFactory _factory;

        public TrackingOccupantActor(
            OccupantId occupant,
            OccupantType occupantType,
            TrackingOccupantFactory factory)
        {
            _occupant = occupant;
            _occupantType = occupantType;
            _factory = factory;

            ReceiveAny(message =>
                _factory.Delivered(new TrackedDelivery(message, _occupant, _occupantType, Self)));
        }

        protected override void PostStop()
        {
            _factory.Stopped(new TrackedChildStopped(_occupant, _occupantType, Self));
            base.PostStop();
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

    private sealed record TrackedDelivery(
        object Message,
        OccupantId Occupant,
        OccupantType OccupantType,
        IActorRef Child);

    private sealed record TrackedChildStopped(
        OccupantId Occupant,
        OccupantType OccupantType,
        IActorRef Child);

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
