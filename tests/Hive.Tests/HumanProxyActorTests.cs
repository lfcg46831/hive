using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class HumanProxyActorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 11, 18, 0, 0, TimeSpan.Zero);
    private static readonly PositionEntityId Entity = PositionEntityId.From(
        OrganizationId.From("acme"),
        PositionId.From("delivery-lead"));
    private static readonly PositionConfigurationStamp Stamp =
        new(1, "sha256:human-proxy-v1");
    private static readonly OccupantId Occupant = OccupantId.From("human:delivery-lead");
    private static readonly UserId User =
        UserId.From(Guid.Parse("11111111-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
    private static readonly OccupantChannelBindingId Binding =
        OccupantChannelBindingId.From(Guid.Parse("22222222-bbbb-bbbb-bbbb-bbbbbbbbbbbb"));

    [Fact]
    public async Task Active_human_occupation_delivers_from_persisted_dispatch_and_keeps_inbox_authoritative()
    {
        var message = Message(
            "33333333-cccc-cccc-cccc-cccccccccccc",
            "44444444-dddd-dddd-dddd-dddddddddddd");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var requestFactory = new RecordingRequestFactory();
        var system = CreateActorSystem("human-proxy-success");
        var reported = new TaskCompletionSource<PositionOccupantChannelDeliveryReported>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var observer = system.ActorOf(
                Props.Create(() => new DeliveryReportObserver(reported)),
                "delivery-report-observer");
            system.EventStream.Subscribe(observer, typeof(PositionOccupantChannelDeliveryReported));
            var actor = CreatePosition(system, activeLink: true, channel, requestFactory);

            await WaitForReadyAsync(actor);
            var accepted = await actor.Ask<AcceptMessageResult>(
                new AcceptMessage(message),
                Timeout());
            var request = await channel.Request.WaitAsync(Timeout());
            var result = await reported.Task.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(AcceptMessageDecision.Accepted, accepted.Decision);
            Assert.Equal(Entity.Organization, request.OrganizationId);
            Assert.Equal(Entity.Position, request.PositionId);
            Assert.Equal(Occupant, request.OccupantId);
            Assert.Equal(User, request.UserId);
            Assert.Equal(Binding, request.OccupantChannelBindingId);
            Assert.Equal(message.Id, request.MessageId);
            Assert.Equal(message.Thread, request.ThreadId);
            Assert.Equal(message, requestFactory.Context!.Message);
            Assert.True(result.Result.IsSuccess);
            Assert.Equal(message.Id, result.MessageId);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Single(events.OfType<OccupantChanged>());
            Assert.Single(events.OfType<MessageDispatched>());
            Assert.Empty(events.OfType<MessageProcessingCompleted>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Structured_channel_failure_is_returned_to_position_without_completing_work()
    {
        var message = Message(
            "55555555-eeee-eeee-eeee-eeeeeeeeeeee",
            "66666666-ffff-ffff-ffff-ffffffffffff");
        var failure = OccupantChannelDeliveryResult.Failed(
            new OccupantChannelDeliveryError(
                OccupantChannelDeliveryErrorCode.ChannelUnavailable,
                isRetryable: true));
        var channel = new RecordingChannel(failure);
        var system = CreateActorSystem("human-proxy-failure");
        var reported = new TaskCompletionSource<PositionOccupantChannelDeliveryReported>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var observer = system.ActorOf(
                Props.Create(() => new DeliveryReportObserver(reported)),
                "delivery-report-observer");
            system.EventStream.Subscribe(observer, typeof(PositionOccupantChannelDeliveryReported));
            var actor = CreatePosition(
                system,
                activeLink: true,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var result = await reported.Task.WaitAsync(Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.True(result.Result.IsFailure);
            Assert.Equal(
                OccupantChannelDeliveryErrorCode.ChannelUnavailable,
                result.Result.Error!.Code);
            Assert.True(result.Result.Error.IsRetryable);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Human_configuration_without_active_link_never_dispatches_and_keeps_message_in_inbox()
    {
        var message = Message(
            "77777777-1111-2222-3333-444444444444",
            "88888888-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var system = CreateActorSystem("human-proxy-unlinked");

        try
        {
            var actor = CreatePosition(
                system,
                activeLink: false,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout());
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Null(state.Occupant);
            Assert.Null(state.OccupantType);
            Assert.Equal([message.Id], state.Inbox.Select(item => item.Id));
            Assert.Empty(state.RecentHistory);
            Assert.False(channel.WasCalled);

            await actor.GracefulStop(Timeout());
            var events = await ReadEventsAsync(system);
            Assert.Empty(events.OfType<OccupantChanged>());
            Assert.Empty(events.OfType<MessageDispatched>());
            Assert.Single(events.OfType<MessageReceived>());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Recovered_human_occupant_with_revoked_link_is_inhibited_without_losing_inbox()
    {
        var pending = Message(
            "99999999-1111-2222-3333-444444444444",
            "aaaaaaaa-5555-6666-7777-888888888888");
        var channel = new RecordingChannel(OccupantChannelDeliveryResult.Succeeded());
        var system = CreateActorSystem("human-proxy-revoked");

        try
        {
            await SeedSnapshotAsync(
                system,
                new PositionSnapshot(
                    At,
                    Occupant,
                    OccupantType.Human,
                    inbox: [pending],
                    recentHistory: [pending.Id],
                    lastConfigurationStamp: Stamp));
            var actor = CreatePosition(
                system,
                activeLink: false,
                channel,
                new RecordingRequestFactory());

            await WaitForReadyAsync(actor);
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());

            Assert.Equal(Occupant, state.Occupant);
            Assert.Equal(OccupantType.Human, state.OccupantType);
            Assert.Equal([pending.Id], state.Inbox.Select(item => item.Id));
            Assert.False(channel.WasCalled);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public void Human_proxy_has_no_recoverable_actor_state()
    {
        Assert.False(typeof(ReceivePersistentActor).IsAssignableFrom(typeof(HumanProxyActor)));
        Assert.All(
            typeof(HumanProxyActor).GetFields(
                System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.DeclaredOnly),
            field => Assert.True(field.IsInitOnly));
    }

    private static IActorRef CreatePosition(
        ActorSystem system,
        bool activeLink,
        IOccupantChannel channel,
        IOccupantChannelDeliveryRequestFactory requestFactory) =>
        system.ActorOf(
            Props.Create(() => new PositionActor(
                Entity.Value,
                new StaticConfigurationProvider(RuntimeConfiguration(activeLink)),
                new PositionOccupantFactory(channel, requestFactory),
                () => At)),
            $"position-{Guid.NewGuid():N}");

    private static PositionRuntimeConfiguration RuntimeConfiguration(bool activeLink) =>
        new(
            Stamp,
            Entity.Organization,
            Entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("delivery"),
                reportsTo: PositionId.From("owner"),
                name: "Delivery lead",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.Human,
                configuredIdentity: Occupant,
                humanIdentity: activeLink
                    ? new HumanOccupantRuntimeIdentity(User, Binding)
                    : null),
            new PositionAuthorityRuntimeConfiguration(Array.Empty<string>()));

    private static Memo Message(string messageId, string threadId) =>
        new(
            Hive.Domain.Identity.MessageId.From(Guid.Parse(messageId)),
            Entity.Organization,
            new PositionEndpointRef(PositionId.From("owner")),
            new PositionEndpointRef(Entity.Position),
            Hive.Domain.Identity.ThreadId.From(Guid.Parse(threadId)),
            Priority.Normal,
            schemaVersion: 1,
            At,
            deadline: null,
            body: "A delivery decision needs human attention.");

    private static ActorSystem CreateActorSystem(string prefix) =>
        ActorSystem.Create(
            $"{prefix}-{Guid.NewGuid():N}",
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

    private static async Task SeedSnapshotAsync(ActorSystem system, PositionSnapshot snapshot)
    {
        var seeder = system.ActorOf(
            Props.Create(() => new PersistenceProbe(PositionActor.PersistenceIdFor(Entity.Value))),
            $"seed-{Guid.NewGuid():N}");
        await seeder.Ask<SnapshotSeeded>(new SeedSnapshot(snapshot), Timeout());
        await seeder.GracefulStop(Timeout());
    }

    private static async Task<IReadOnlyList<PositionEvent>> ReadEventsAsync(ActorSystem system)
    {
        var reader = system.ActorOf(
            Props.Create(() => new PersistenceProbe(PositionActor.PersistenceIdFor(Entity.Value))),
            $"reader-{Guid.NewGuid():N}");
        var events = await reader.Ask<IReadOnlyList<PositionEvent>>(ReadEvents.Instance, Timeout());
        await reader.GracefulStop(Timeout());
        return events;
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(PositionRuntimeConfiguration configuration)
        : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(configuration));
    }

    private sealed class RecordingChannel(OccupantChannelDeliveryResult result) : IOccupantChannel
    {
        private readonly TaskCompletionSource<OccupantChannelDeliveryRequest> _request =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<OccupantChannelDeliveryRequest> Request => _request.Task;

        public bool WasCalled => _request.Task.IsCompleted;

        public Task<OccupantChannelDeliveryResult> DeliverAsync(
            OccupantChannelDeliveryRequest request,
            CancellationToken cancellationToken = default)
        {
            _request.TrySetResult(request);
            return Task.FromResult(result);
        }
    }

    private sealed class RecordingRequestFactory : IOccupantChannelDeliveryRequestFactory
    {
        public OccupantChannelDeliveryContext? Context { get; private set; }

        public OccupantChannelDeliveryRequest Create(OccupantChannelDeliveryContext context)
        {
            Context = context;
            return new OccupantChannelDeliveryRequest(
                context.OrganizationId,
                context.PositionId,
                context.OccupantId,
                context.UserId,
                context.OccupantChannelBindingId!,
                context.Message.Id,
                context.Message.Thread,
                "A message is waiting in your HIVE inbox.",
                OccupantChannelCorrelationToken.From("opaque.test.token"));
        }
    }

    private sealed class DeliveryReportObserver : ReceiveActor
    {
        public DeliveryReportObserver(
            TaskCompletionSource<PositionOccupantChannelDeliveryReported> completion)
        {
            Receive<PositionOccupantChannelDeliveryReported>(completion.TrySetResult);
        }
    }

    private sealed class PersistenceProbe : ReceivePersistentActor
    {
        private readonly List<PositionEvent> _events = [];
        private IActorRef? _snapshotReplyTo;

        public PersistenceProbe(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<PositionEvent>(_events.Add);
            RecoverAny(_ => { });
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
