using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Actors.Serialization;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Domain.Auditing;
using Hive.Infrastructure.Auditing;
using static Hive.Tests.PeerResponseRoutingValidatorTests;

namespace Hive.Tests;

public sealed partial class HorizontalRoutingAdmissionTests
{
    [Theory]
    [InlineData(PeerChannelRejectionAction.None, true)]
    [InlineData(PeerChannelRejectionAction.Escalate, false)]
    public async Task Rejection_without_declared_escalation_leaves_decision_to_occupant(
        PeerChannelRejectionAction action, bool exists)
    {
        using var system = System();
        try
        {
            var contracts = new Contracts { OnRejection = action, Allowed = exists, Types = [PeerChannelMessageType.Memo] };
            var registrar = new Registrar();
            var actor = Create(system, "responder", contracts, registrar, new Projections());
            Assert.False((await actor.Ask<AcceptMessageResult>(new AcceptMessage(Request()), Timeout)).IsAccepted);
            Assert.Empty((await State(actor)).PeerRejectionEscalations);
            Assert.Empty(registrar.Rejections);
        }
        finally { await system.Terminate(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Structural_request_rejection_escalates_once_with_sanitized_correlated_context(bool limit)
    {
        using var system = System();
        try
        {
            var contracts = new Contracts { OnRejection = PeerChannelRejectionAction.Escalate, Limit = 1,
                Types = limit ? [PeerChannelMessageType.PeerRequest] : [PeerChannelMessageType.Memo] };
            var registrar = new Registrar();
            var projections = new Projections();
            var emitter = new EscalationEmitter();
            var requester = Create(system, "requester", contracts, registrar, projections, emitter);
            registrar.Actors["requester"] = requester;
            var recipient = Create(system, "responder", contracts, registrar, projections);
            if (limit)
                Assert.True((await recipient.Ask<AcceptMessageResult>(new AcceptMessage(Request()), Timeout)).IsAccepted);

            var request = Request();
            var reason = limit ? RejectionReason.LimitExceeded : RejectionReason.InvalidRoute;
            var notification = new RecordPeerRequestRejection(request, reason);
            var rejected = await recipient.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout);
            Assert.Equal(reason, rejected.Reason);
            await Eventually(async () => (await State(recipient)).PeerRejectionEscalations[notification.EscalationId].Completed
                && (await State(requester)).PeerRejectionEscalations[notification.EscalationId].Completed);
            var escalation = Assert.Single(emitter.Messages);
            Assert.Equal(request.From, escalation.From);
            Assert.Equal(new PositionEndpointRef(PositionId.From("source-lead")), escalation.To);
            Assert.Equal(request.Thread, escalation.Thread);
            Assert.Equal(request.Priority, escalation.Priority);
            Assert.Equal(notification.EscalationId, escalation.Id);
            Assert.Null(escalation.Deadline);
            Assert.Contains(request.Id.ToString(), escalation.Context);
            Assert.Contains(RejectionReasonContract.ToWireValue(reason), escalation.Context);
            Assert.DoesNotContain(request.Ask, escalation.Context);
            Assert.DoesNotContain("max_open_requests", escalation.Context);
            Assert.Empty(escalation.OptionsConsidered);
            Assert.True((await new EscalationRoutingValidator(Relations()).ValidateAsync(escalation)).IsValid);
            var audit = new PeerEscalationAuditLog();
            var publisher = new JourneyAuditPositionProjectionPublisher(audit);
            foreach (var committed in projections.Events.OfType<PositionEventCommitted>())
                publisher.Publish(committed);
            var created = Assert.Single(audit.Records.Where(record => record.Stage == JourneyAuditStage.ResultMessageCreated));
            Assert.Equal(escalation.Id, created.MessageId);
            Assert.DoesNotContain(request.Ask, string.Join(" ", created.Payload.Values));

            var another = new PeerRequest(MessageId.New(), Org, request.From, request.To, request.Thread,
                Priority.Normal, 1, At, null, "Different confidential body");
            await recipient.Ask<AcceptMessageResult>(new AcceptMessage(another), Timeout);
            Assert.Single((await State(requester)).PeerRejectionEscalations);
            Assert.Single(emitter.Messages);
            Assert.Equal(2, projections.Events.OfType<PositionMessageRoutingRejected>().Count());
            Assert.DoesNotContain(request.Id, (await State(recipient)).ProcessedMessages);

            await requester.GracefulStop(Timeout);
            requester = Create(system, "requester", contracts, registrar, projections, emitter);
            registrar.Actors["requester"] = requester;
            Assert.True((await requester.Ask<AcceptMessageResult>(notification, Timeout)).IsAccepted);
            Assert.True((await State(requester)).PeerRejectionEscalations[notification.EscalationId].Completed);
            Assert.Single(emitter.Messages);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Notification_and_escalation_outboxes_recover_from_snapshot_and_retry_failures()
    {
        using var system = System();
        try
        {
            var contracts = new Contracts { OnRejection = PeerChannelRejectionAction.Escalate, Types = [PeerChannelMessageType.Memo] };
            var registrar = new Registrar { Unavailable = true };
            var projections = new Projections();
            var emitter = new EscalationEmitter { Unavailable = true };
            var requester = Create(system, "requester", contracts, registrar, projections, emitter);
            registrar.Actors["requester"] = requester;
            var recipient = Create(system, "responder", contracts, registrar, projections);
            var request = Request();
            var notification = new RecordPeerRequestRejection(request, RejectionReason.InvalidRoute);
            await recipient.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout);
            await Eventually(() => Task.FromResult(!registrar.Rejections.IsEmpty));
            var recipientSnapshot = (await State(recipient)).ToSnapshot(At);
            Assert.False(Assert.Single(recipientSnapshot.PeerRejectionEscalations).Completed);
            await recipient.GracefulStop(Timeout);
            await SeedPeerEscalationSnapshot(system, "responder", recipientSnapshot);
            contracts.Allowed = false; // A persisted escalate decision survives channel removal.
            registrar.Unavailable = false;
            recipient = Create(system, "responder", contracts, registrar, projections);
            await Eventually(() => Task.FromResult(!emitter.Messages.IsEmpty));
            var senderSnapshot = (await State(requester)).ToSnapshot(At);
            var first = Assert.Single(senderSnapshot.PeerRejectionEscalations).Escalation!;
            Assert.False(Assert.Single(senderSnapshot.PeerRejectionEscalations).Completed);
            await requester.GracefulStop(Timeout);
            await SeedPeerEscalationSnapshot(system, "requester", senderSnapshot);
            // Recovery still fails once; the timer must subsequently retry without another command.
            requester = Create(system, "requester", contracts, registrar, projections, emitter);
            registrar.Actors["requester"] = requester;
            var attempts = emitter.Messages.Count;
            await Eventually(() => Task.FromResult(emitter.Messages.Count > attempts));
            emitter.Unavailable = false;
            await Eventually(async () => (await State(requester)).PeerRejectionEscalations[notification.EscalationId].Completed);
            Assert.All(emitter.Messages, escalation => Assert.Equal(first, escalation));
            await Eventually(async () => (await State(recipient)).PeerRejectionEscalations[notification.EscalationId].Completed);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Root_records_no_superior_terminal_and_internal_notification_is_scoped()
    {
        using var system = System();
        try
        {
            var contracts = new Contracts();
            var registrar = new Registrar();
            var emitter = new EscalationEmitter();
            var actor = Create(system, "source-lead", contracts, registrar, new Projections(), emitter);
            var original = Request();
            await Assert.ThrowsAsync<InvalidOperationException>(() => actor.Ask<AcceptMessageResult>(
                new RecordPeerRequestRejection(original, RejectionReason.InvalidRoute), Timeout));
            var request = new PeerRequest(MessageId.New(), Org, new PositionEndpointRef(PositionId.From("source-lead")),
                original.To, original.Thread, Priority.Normal, 1, At, null, "Context");
            var command = new RecordPeerRequestRejection(request, RejectionReason.InvalidRoute);
            Assert.True((await actor.Ask<AcceptMessageResult>(command, Timeout)).IsAccepted);
            var terminal = Assert.Single((await State(actor)).PeerRejectionEscalations).Value;
            Assert.True(terminal.Completed);
            Assert.Null(terminal.Escalation);
            Assert.Empty(emitter.Messages);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Policy_ignores_memo_responses_and_unrelated_errors_and_propagates_failures()
    {
        var contracts = new Contracts { Failure = new InvalidOperationException("registry unavailable") };
        var policy = new PeerRequestRejectionPolicy(Relations(), contracts);
        var request = Request();
        RoutingRejection Rejection(OrgMessage message, ValidationError error) => RoutingRejection.Create(
            RoutingValidationContext.ForMessage(message), ValidationResult.Create([error]));
        var structural = Rejection(request, RoutingValidationCatalog.PeerChannelRequired());
        Assert.False(await policy.ShouldEscalateAsync(Memo(request), structural));
        Assert.False(await policy.ShouldEscalateAsync(Response(request), structural));
        Assert.False(await policy.ShouldEscalateAsync(request,
            Rejection(request, RoutingValidationCatalog.EndpointNotAllowed("from"))));
        Assert.Same(contracts.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            policy.ShouldEscalateAsync(request, structural).AsTask()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.ShouldEscalateAsync(request, structural, cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Rejection_identity_and_protocol_preserve_dedupe_and_backward_compatible_snapshots()
    {
        var request = Request();
        var notification = new RecordPeerRequestRejection(request, RejectionReason.InvalidRoute);
        Assert.NotEqual(notification.EscalationId,
            new RecordPeerRequestRejection(request, RejectionReason.LimitExceeded).EscalationId);
        Assert.NotEqual(notification.EscalationId,
            new RecordPeerRequestRejection(Request(), RejectionReason.InvalidRoute).EscalationId);
        var work = new PeerRejectionEscalationUpdated(notification, null, false, At);
        var escalation = new Escalation(notification.EscalationId, Org, request.From,
            new PositionEndpointRef(PositionId.From("source-lead")), request.Thread, request.Priority,
            1, At, null, "Horizontal peer request rejected.", "Sanitized context", []);
        var senderWork = new PeerRejectionEscalationUpdated(notification, escalation, false, At);
        var done = new PeerRejectionEscalationUpdated(notification, null, true, At);
        var state = PositionState.Empty.Apply(work).Apply(new ShortMemoryUpdated("key", "value", At));
        Assert.Single(state.PeerRejectionEscalations);
        var configuration = (await new Provider().LoadAsync(
            PositionEntityId.From(Org, PositionId.From("requester")), CancellationToken.None)).Configuration!;
        Assert.Contains(PositionPassivationBlockReason.PendingDelivery, state.EvaluatePassivation(configuration).BlockReasons);
        Assert.True(state.Apply(done).EvaluatePassivation(configuration).IsAllowed);
        Assert.True(PositionState.Restore(state.Apply(done).ToSnapshot(At)).Apply(work)
            .PeerRejectionEscalations[notification.EscalationId].Completed);
        object[] values = [notification, work, done, senderWork, state.ToSnapshot(At),
            PositionState.Empty.Apply(senderWork).ToSnapshot(At),
            PositionEnvelope.For(PositionEntityId.From(Org, PositionId.From("requester")), notification)];
        foreach (var value in values)
        {
            var bytes = PositionProtocolJsonFormat.Serialize(value);
            var restored = PositionProtocolJsonFormat.Deserialize(PositionProtocolManifests.ForType(value.GetType()), bytes);
            Assert.Equal(bytes, PositionProtocolJsonFormat.Serialize(restored));
        }
        var old = PositionProtocolJsonFormat.Serialize(new PositionSnapshot(At));
        Assert.DoesNotContain("PeerRejectionEscalations", global::System.Text.Encoding.UTF8.GetString(old), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(((PositionSnapshot)PositionProtocolJsonFormat.Deserialize("position-snapshot", old)).PeerRejectionEscalations);
    }

    private sealed class EscalationEmitter : IPositionMessageEmitter
    {
        public ConcurrentQueue<Escalation> Messages { get; } = new();
        public bool Unavailable { get; set; }
        public void Emit(ActorSystem system, OrgMessage message) => throw new InvalidOperationException("Confirmation required");
        public ValueTask<AcceptMessageResult> EmitConfirmedAsync(ActorSystem system, OrgMessage message)
        {
            Messages.Enqueue(Assert.IsType<Escalation>(message));
            return Unavailable ? ValueTask.FromException<AcceptMessageResult>(new InvalidOperationException("leader unavailable"))
                : ValueTask.FromResult(AcceptMessageResult.AlreadyAccepted(message.Id));
        }
    }

    private sealed class PeerEscalationAuditLog : IJourneyAuditLog
    {
        public List<JourneyAuditRecord> Records { get; } = [];
        public void Append(JourneyAuditRecord record) => Records.Add(record);
        public IReadOnlyList<JourneyAuditRecord> ReadByThread(ThreadId threadId, DirectiveId? directiveId = null) =>
            Records.Where(record => record.ThreadId == threadId
                && (directiveId is null || record.DirectiveId == directiveId)).ToArray();
    }

    private static async Task SeedPeerEscalationSnapshot(ActorSystem system, string position, PositionSnapshot snapshot)
    {
        var seeder = system.ActorOf(Props.Create(() => new PeerEscalationSnapshotSeeder(
            PositionActor.PersistenceIdFor(PositionEntityId.From(Org, PositionId.From(position)).Value))));
        await seeder.Ask<bool>(snapshot, Timeout);
        await seeder.GracefulStop(Timeout);
    }

    private sealed class PeerEscalationSnapshotSeeder : ReceivePersistentActor
    {
        private IActorRef _replyTo = ActorRefs.Nobody;
        public PeerEscalationSnapshotSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<PositionEvent>(_ => { });
            Command<PositionSnapshot>(snapshot => { _replyTo = Sender; SaveSnapshot(snapshot); });
            Command<SaveSnapshotSuccess>(_ => _replyTo.Tell(true));
            Command<SaveSnapshotFailure>(failure => _replyTo.Tell(new Status.Failure(failure.Cause)));
        }
        public override string PersistenceId { get; }
    }
}
