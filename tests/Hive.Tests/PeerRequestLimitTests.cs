using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Actors.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Auditing;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;

namespace Hive.Tests;

public sealed class PeerRequestLimitTests
{
    private static readonly OrganizationId Org = OrganizationId.From("limits");
    private static readonly UnitId Source = UnitId.From("source");
    private static readonly UnitId Destination = UnitId.From("destination");
    private static readonly DateTimeOffset At = DateTimeOffset.Parse("2026-09-11T10:00:00Z");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static readonly PeerRequestChannel Channel = new(Source, Destination, 1);

    [Fact]
    public void Capacity_survives_delivery_history_eviction_snapshot_and_reply_replay()
    {
        var request = Request(deadline: At.AddHours(1));
        var original = PositionState.Empty.Apply(new MessageReceived(request, At, Channel));
        var state = original;
        state = state.Apply(new MessageDispatched(request.Id, request.Thread,
            OccupantId.From("worker"), OccupantType.Human, At));
        state = state.Apply(new MessageProcessingCompleted("processing", request.Id, request.Thread,
            MessageProcessingCompletionStatus.Completed, At));
        Assert.Equal(1, state.CountOpenPeerRequests(Channel, At));
        // A snapshot retaining no delivery history still retains all channel capacity records.
        state = PositionState.Restore(new PositionSnapshot(At,
            processedMessages: state.ProcessedMessages, receivedPeerRequests: state.ReceivedPeerRequests.Values));
        Assert.DoesNotContain(request, state.MaterializedHistory);
        Assert.Equal(1, state.CountOpenPeerRequests(Channel, At));
        state = RoundTrip(state);
        Assert.Equal(1, state.CountOpenPeerRequests(Channel, At));
        var response = PeerResponseRoutingValidatorTests.Response(request);
        var emitted = new OccupantReplyEmitted(request.Id,
            OccupantReplyAuthor.HumanUser("person", "web"), response, At);
        state = RoundTrip(state.Apply(emitted)).Apply(new MessageReceived(request, At, Channel));
        Assert.Equal(0, state.CountOpenPeerRequests(Channel, At));
        Assert.Equal(1, original.CountOpenPeerRequests(Channel, At));
        Assert.Same(original, original.Apply(new PeerRequestClosed(MessageId.New(), MessageState.Failed, At)));
    }

    [Fact]
    public void Counts_use_original_direction_and_deadline_and_only_correlated_emission_closes()
    {
        var request = Request(deadline: At.AddTicks(1));
        var state = PositionState.Empty.Apply(new MessageReceived(request, At, Channel));
        Assert.Equal(1, state.CountOpenPeerRequests(new(Source, Destination, 99), At));
        Assert.Equal(0, state.CountOpenPeerRequests(new(Destination, Source, 1), At));
        var otherChannel = new PeerRequestChannel(UnitId.From("another-unit"), Destination, 1);
        state = state.Apply(new MessageReceived(Request("another-requester"), At, otherChannel));
        Assert.Equal(1, state.CountOpenPeerRequests(Channel, At));
        Assert.Equal(1, state.CountOpenPeerRequests(otherChannel, At));
        Assert.Equal(0, state.CountOpenPeerRequests(Channel, At.AddTicks(1)));
        Assert.Equal(0, state.CountOpenPeerRequests(Channel, At.AddHours(1)));
        var badResponse = PeerResponseRoutingValidatorTests.Response(request, thread: ThreadId.New());
        state = state.Apply(new OccupantReplyEmitted(request.Id,
            OccupantReplyAuthor.HumanUser("person", "web"), badResponse, At));
        Assert.Equal(1, state.CountOpenPeerRequests(Channel, At));
        var noDeadline = PositionState.Empty.Apply(new MessageReceived(Request(), At, Channel));
        Assert.Equal(1, noDeadline.CountOpenPeerRequests(Channel, At.AddYears(1)));
    }

    [Fact]
    public void Wire_evolution_preserves_old_payloads_and_new_rejection_is_sanitized()
    {
        var request = Request();
        var oldEvent = new MessageReceived(request, At);
        Assert.DoesNotContain("PeerChannel", System.Text.Encoding.UTF8.GetString(PositionProtocolJsonFormat.Serialize(oldEvent)));
        Assert.Null(((MessageReceived)RoundTrip(oldEvent)).PeerChannel);
        Assert.Empty(((PositionSnapshot)RoundTrip(new PositionSnapshot(At))).ReceivedPeerRequests);
        var received = new MessageReceived(request, At, Channel);
        Assert.Equal(received, RoundTrip(received));
        foreach (var result in new[] { AcceptMessageResult.Accepted(request.Id),
            AcceptMessageResult.AlreadyAccepted(request.Id),
            AcceptMessageResult.Rejected(request.Id, RejectionReason.LimitExceeded) })
            Assert.Equal(result, RoundTrip(result));
        var rejection = RoutingRejection.Create(RoutingValidationContext.ForMessage(request),
            ValidationResult.Create([RoutingValidationCatalog.PeerChannelLimitExceeded()]));
        Assert.Equal(new ValidationError("limit-exceeded", "$", RejectionReason.LimitExceeded),
            Assert.Single(rejection.PublicResult.Errors));
        var audit = RoutingRejectionAuditEvent.FromRejection(rejection, At);
        Assert.Equal(RoutingValidationCatalog.PeerChannelLimitExceeded(), Assert.Single(audit.Errors));
        Assert.Equal(request.Id, audit.MessageId);
        Assert.Equal(request.Thread, audit.Thread);
        var auditLog = new AuditLog();
        new JourneyAuditPositionProjectionPublisher(auditLog).Publish(new PositionMessageRoutingRejected(
            PositionEntityId.From(Org, PositionId.From("responder")), rejection, At));
        var persistedAudit = Assert.Single(auditLog.Records);
        Assert.Equal(JourneyAuditOutcome.Rejected, persistedAudit.Outcome);
        Assert.Equal("peer-channel-limit-exceeded", persistedAudit.ReasonCode);
        Assert.Equal(request.Id, persistedAudit.MessageId);
        Assert.Contains("peer-channel-limit-exceeded@to.positionId:limit-exceeded", persistedAudit.Payload["errors"]);
        Assert.DoesNotContain(request.Ask, string.Join(",", persistedAudit.Payload.Values));
        Assert.Throws<ArgumentException>(() => new PositionSnapshot(At,
            receivedPeerRequests: [new(request, Channel), new(request, Channel)]));
    }

    [Theory]
    [InlineData("requester", "responder", true)]
    [InlineData("requester", "requester2", false)]
    [InlineData("source-lead", "destination-lead", false)]
    [InlineData("responder", "requester", false)]
    public async Task Resolver_limits_only_declared_direction_and_exempts_same_unit_and_leadership(
        string from, string to, bool limited)
    {
        Assert.Equal(limited, await Resolver().ResolveAsync(Request(from, to)) is not null);
    }

    [Fact]
    public async Task Resolver_propagates_cancellation_and_technical_failure()
    {
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Resolver().ResolveAsync(Request(), canceled.Token).AsTask());
        var contracts = new DelayedContracts { Failure = new InvalidOperationException("offline") };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Resolver(contracts).ResolveAsync(Request()).AsTask());
    }

    [Fact]
    public async Task Actor_serializes_concurrent_admissions_audits_rejection_and_recovers_capacity()
    {
        using var system = ActorSystem.Create("limits-" + Guid.NewGuid().ToString("N"),
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
            var contracts = new DelayedContracts();
            var projections = new Projections();
            var entity = PositionEntityId.From(Org, PositionId.From("responder"));
            var now = At;
            IActorRef Create(PositionEntityId id) => system.ActorOf(Props.Create(() => new PositionActor(
                id.Value, new Provider(), PositionOccupantFactory.Instance, projections, () => now,
                null, new AllowReplies(), new IgnoreEmitter(), null, null, null, Resolver(contracts))));
            var actor = Create(entity);
            var requests = Enumerable.Range(0, 10).Select(i => Request(i % 2 == 0 ? "requester" : "requester2",
                deadline: At.AddMinutes(1))).ToArray();
            var results = await Task.WhenAll(requests.Select(request =>
                actor.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)));
            Assert.Single(results.Where(result => result.IsAccepted));
            Assert.Equal(9, results.Count(result => result.Reason == RejectionReason.LimitExceeded));
            Assert.Equal(9, projections.Events.OfType<PositionMessageRoutingRejected>().Count());
            var accepted = requests.Single(request => results.Single(result => result.MessageId == request.Id).IsAccepted);
            var retry = requests.First(request => request.Id != accepted.Id);
            contracts.Failure = new InvalidOperationException("registry offline");
            Assert.Equal(AcceptMessageDecision.AlreadyAccepted,
                (await actor.Ask<AcceptMessageResult>(new AcceptMessage(accepted), Timeout)).Decision);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                actor.Ask<AcceptMessageResult>(new AcceptMessage(retry), Timeout));
            contracts.Failure = null;
            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout);
            Assert.DoesNotContain(retry.Id, state.ProcessedMessages);
            Assert.Equal(1, state.CountOpenPeerRequests(Channel, At));
            await actor.GracefulStop(Timeout);
            actor = Create(entity);
            Assert.False((await actor.Ask<AcceptMessageResult>(new AcceptMessage(retry), Timeout)).IsAccepted);

            var snapshot = (await actor.Ask<PositionState>(GetPositionState.Instance, Timeout)).ToSnapshot(At);
            await actor.GracefulStop(Timeout);
            var seeder = system.ActorOf(Props.Create(() => new SnapshotSeeder(PositionActor.PersistenceIdFor(entity.Value))));
            await seeder.Ask<bool>(snapshot, Timeout);
            await seeder.GracefulStop(Timeout);
            actor = Create(entity);
            Assert.False((await actor.Ask<AcceptMessageResult>(new AcceptMessage(retry), Timeout)).IsAccepted);
            var response = await actor.Ask<OccupantReplyEmissionResult>(new EmitOccupantReply(accepted.Id,
                MessageId.New(), OccupantReplyAuthor.HumanUser("person", "web"), "Reviewed."), Timeout);
            Assert.True(response.IsAccepted);
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(retry), Timeout)).IsAccepted);
            await actor.GracefulStop(Timeout);
            actor = Create(entity);
            Assert.False((await actor.Ask<AcceptMessageResult>(new AcceptMessage(Request()), Timeout)).IsAccepted);
            now = At.AddMinutes(1);
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(Request()), Timeout)).IsAccepted);

            var second = Create(PositionEntityId.From(Org, PositionId.From("responder2")));
            Assert.True((await second.Ask<AcceptMessageResult>(new AcceptMessage(Request(to: "responder2")), Timeout)).IsAccepted);
            var sameUnit = Request("responder2");
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(sameUnit), Timeout)).IsAccepted);
        }
        finally { await system.Terminate(); }
    }

    private static PeerRequest Request(string from = "requester", string to = "responder", DateTimeOffset? deadline = null) =>
        new(MessageId.New(), Org, new PositionEndpointRef(PositionId.From(from)), new PositionEndpointRef(PositionId.From(to)),
            ThreadId.New(), Priority.Normal, 1, At, deadline, "Please review");

    private static MaterializedOrganizationRelations Relations() => new(OrganizationRelationsSnapshot
        .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
        .AddPosition(PositionId.From("source-lead"), Source)
        .AddPosition(PositionId.From("requester"), Source, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("requester2"), Source, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("destination-lead"), Destination, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("responder"), Destination, PositionId.From("destination-lead"))
        .AddPosition(PositionId.From("responder2"), Destination, PositionId.From("destination-lead"))
        .AddUnitLeadership(Destination, PositionId.From("destination-lead")).Build());

    private static PeerRequestLimitResolver Resolver(IPeerChannelContracts? contracts = null) => new(Relations(),
        contracts ?? new MaterializedPeerChannelContracts(PeerChannelContractsSnapshot.CreateBuilder(Org)
            .AddUnit(Source, []).AddUnit(Destination, [new(Source, [PeerChannelMessageType.PeerRequest],
                1, PeerChannelRejectionAction.None)]).Build()));

    private static object RoundTrip(object value) => PositionProtocolJsonFormat.Deserialize(
        PositionProtocolManifests.ForType(value.GetType()), PositionProtocolJsonFormat.Serialize(value));
    private static PositionState RoundTrip(PositionState state) => PositionState.Restore((PositionSnapshot)RoundTrip(state.ToSnapshot(At)));

    private sealed class DelayedContracts : IPeerChannelContracts
    {
        public Exception? Failure { get; set; }
        public async ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(OrganizationId organizationId,
            UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default)
        {
            await Task.Delay(5, cancellationToken);
            if (Failure is not null) throw Failure;
            return new PeerChannelConfiguration(fromUnitId, [PeerChannelMessageType.PeerRequest], 1, PeerChannelRejectionAction.None);
        }
    }
    private sealed class Projections : IPositionProjectionPublisher
    {
        public System.Collections.Concurrent.ConcurrentQueue<PositionProjectionEvent> Events { get; } = new();
        public void Publish(PositionProjectionEvent @event) => Events.Enqueue(@event);
    }
    private sealed class AuditLog : IJourneyAuditLog
    {
        public List<JourneyAuditRecord> Records { get; } = [];
        public void Append(JourneyAuditRecord record) => Records.Add(record);
        public IReadOnlyList<JourneyAuditRecord> ReadByThread(ThreadId threadId, DirectiveId? directiveId = null) =>
            Records.Where(record => record.ThreadId == threadId).ToArray();
    }
    private sealed class AllowReplies : IOccupantReplyMessageValidator
    {
        public ValueTask<ValidationResult> ValidateAsync(PositionState state, OrgMessage message,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(ValidationResult.Valid);
    }
    private sealed class IgnoreEmitter : IPositionMessageEmitter
    {
        public void Emit(ActorSystem system, OrgMessage message) { }
    }
    private sealed class Provider : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(PositionEntityId entity,
            CancellationToken cancellationToken) => Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(
            new PositionRuntimeConfiguration(new PositionConfigurationStamp(1, "sha256:limits"), entity.Organization,
                entity.Position, new PositionRuntimeDescriptor(Destination, null, entity.Position.Value, "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(OccupantType.Human), new PositionAuthorityRuntimeConfiguration([]))));
    }
    private sealed class SnapshotSeeder : ReceivePersistentActor
    {
        private IActorRef? _replyTo;
        public SnapshotSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            RecoverAny(_ => { });
            Command<PositionSnapshot>(snapshot => { _replyTo = Sender; SaveSnapshot(snapshot); });
            Command<SaveSnapshotSuccess>(_ => _replyTo!.Tell(true));
            Command<SaveSnapshotFailure>(failure => _replyTo!.Tell(new Status.Failure(failure.Cause)));
        }
        public override string PersistenceId { get; }
    }
}
