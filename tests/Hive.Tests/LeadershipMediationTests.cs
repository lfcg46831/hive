using System.Collections.Concurrent;
using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;
using Directive = Hive.Domain.Messaging.Directive;
using static Hive.Tests.PeerResponseRoutingValidatorTests;

namespace Hive.Tests;

public sealed partial class HorizontalRoutingAdmissionTests
{
    [Theory]
    [InlineData("absent", false)]
    [InlineData("absent", true)]
    [InlineData("restricted", false)]
    [InlineData("restricted", true)]
    [InlineData("limited", false)]
    [InlineData("limited", true)]
    [InlineData("unavailable", false)]
    [InlineData("unavailable", true)]
    public async Task Leadership_mediation_is_implicit_audited_and_returns_through_vertical_directives(
        string policy, bool reverse)
    {
        using var system = System();
        try
        {
            var contracts = new MediationContracts(policy);
            var relations = SiblingRelations();
            var registrar = new Registrar();
            var audit = new MediationAuditLog();
            var projections = new Projections();
            foreach (var position in new[] { "source-lead", "destination-lead", "requester", "responder", "root-lead" })
                registrar.Actors[position] = CreateMediationActor(system, position, relations, contracts, registrar,
                    new JourneyAuditPositionProjectionPublisher(audit, projections));

            var from = reverse ? "destination-lead" : "source-lead";
            var to = reverse ? "source-lead" : "destination-lead";
            var requester = registrar.Actors[from];
            var recipient = registrar.Actors[to];
            var thread = ThreadId.New();
            var request = MediationRequest(from, to, thread);
            var memo = Memo(request);
            Assert.True((await recipient.Ask<AcceptMessageResult>(new AcceptMessage(memo), Timeout)).IsAccepted);

            // More outstanding requests than the declared limit, all in the same thread.
            var requests = new[] { request, MediationRequest(from, to, thread), MediationRequest(from, to, thread) };
            foreach (var pending in requests)
                Assert.True((await recipient.Ask<AcceptMessageResult>(new AcceptMessage(pending), Timeout)).IsAccepted);
            await Eventually(async () => (await State(requester)).PeerRequests.Count == requests.Length);
            Assert.Empty((await State(recipient)).ReceivedPeerRequests);
            Assert.Equal(0, contracts.Calls);
            Assert.Equal(AcceptMessageDecision.AlreadyAccepted,
                (await recipient.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)).Decision);

            var response = Response(request);
            Assert.True((await requester.Ask<AcceptMessageResult>(new AcceptMessage(response), Timeout)).IsAccepted);
            Assert.Equal(MessageState.Completed, (await State(requester)).PeerRequests[request.Id].State);
            Assert.Equal(RejectionReason.Duplicate,
                (await requester.Ask<AcceptMessageResult>(new AcceptMessage(Response(request)), Timeout)).Reason);
            var orphan = Response(MediationRequest(from, to, thread));
            Assert.Equal(RejectionReason.InvalidRoute,
                (await requester.Ask<AcceptMessageResult>(new AcceptMessage(orphan), Timeout)).Reason);

            Assert.Empty((await State(registrar.Actors["requester"])).Inbox);
            Assert.Empty((await State(registrar.Actors["responder"])).Inbox);
            Assert.Empty((await State(registrar.Actors["root-lead"])).Inbox);
            Assert.Empty(registrar.Rejections);
            Assert.DoesNotContain(projections.Events.OfType<PositionEventCommitted>(),
                item => item.Event is OccupantReplyEmitted { Message: Directive });

            // Each leader explicitly issues the agreed work to its own direct subordinate.
            var vertical = new DirectiveRoutingValidator(relations);
            var directives = new[]
            {
                MediationDirective("source-lead", "requester", thread),
                MediationDirective("destination-lead", "responder", thread),
            };
            foreach (var directive in directives)
            {
                Assert.True((await vertical.ValidateAsync(directive)).IsValid);
                var target = ((PositionEndpointRef)directive.To).PositionId.Value;
                Assert.True((await registrar.Actors[target].Ask<AcceptMessageResult>(
                    new AcceptMessage(directive), Timeout)).IsAccepted);
                Assert.Contains(directive, (await State(registrar.Actors[target])).Inbox);
            }
            foreach (var (leader, worker) in new[] { ("source-lead", "responder"), ("destination-lead", "requester") })
                Assert.Equal("direct-subordinate-required", Assert.Single((await vertical.ValidateAsync(
                    MediationDirective(leader, worker, thread))).Errors).Code);

            var records = audit.ReadByThread(thread);
            foreach (var message in requests.Cast<OrgMessage>().Append(memo).Append(response).Concat(directives))
            {
                var accepted = Assert.Single(records.Where(record => record.MessageId == message.Id
                    && record.Stage == JourneyAuditStage.PositionAccepted && record.Outcome == JourneyAuditOutcome.Accepted));
                Assert.Equal(Org, accepted.OrganizationId);
                Assert.Equal(((PositionEndpointRef)message.To).PositionId, accepted.PositionId);
                Assert.Equal(message.GetType().Name, accepted.MessageType);
                Assert.Equal(message.Channel.ToString(), accepted.Payload["channel"]);
            }
            var rejected = records.Where(record => record.Outcome == JourneyAuditOutcome.Rejected).ToArray();
            Assert.Equal(2, rejected.Length);
            Assert.All(rejected, record => Assert.Equal(nameof(PeerResponse), record.MessageType));
            Assert.Contains(rejected, record => record.ReasonCode == "peer-response-duplicate");
            Assert.Contains(rejected, record => record.ReasonCode == "peer-request-not-found" && record.MessageId == orphan.Id);
            var payload = string.Join("|", records.SelectMany(record => record.Payload.Values));
            Assert.DoesNotContain(request.Ask, payload);
            Assert.DoesNotContain(response.Body, payload);
            Assert.DoesNotContain(directives[0].Objective, payload);
            Assert.Equal(0, contracts.Calls);
        }
        finally { await system.Terminate(); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task One_leader_does_not_grant_mediation_and_rejection_audit_preserves_message_type(
        bool memo, bool reverse)
    {
        using var system = System();
        try
        {
            var from = reverse ? "responder" : "source-lead";
            var to = reverse ? "source-lead" : "responder";
            var request = MediationRequest(from, to, ThreadId.New());
            OrgMessage message = memo ? Memo(request) : request;
            var audit = new MediationAuditLog();
            var actor = CreateMediationActor(system, to, SiblingRelations(), new MediationContracts("absent"),
                new Registrar(), new JourneyAuditPositionProjectionPublisher(audit));
            var result = await actor.Ask<AcceptMessageResult>(new AcceptMessage(message), Timeout);
            Assert.Equal(RejectionReason.InvalidRoute, result.Reason);
            Assert.Empty((await State(actor)).Inbox);
            var rejection = Assert.Single(audit.ReadByThread(message.Thread));
            Assert.Equal(message.GetType().Name, rejection.MessageType);
            Assert.Equal(message.Id, rejection.MessageId);
            Assert.Equal(Org, rejection.OrganizationId);
            Assert.Equal(JourneyAuditOutcome.Rejected, rejection.Outcome);
            Assert.Equal("peer-channel-required", rejection.ReasonCode);
            Assert.DoesNotContain(request.Ask, string.Join("|", rejection.Payload.Values));
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public void Legacy_routing_rejection_projection_does_not_invent_a_message_type()
    {
        var request = MediationRequest("source-lead", "responder", ThreadId.New());
        var audit = new MediationAuditLog();
        new JourneyAuditPositionProjectionPublisher(audit).Publish(new PositionMessageRoutingRejected(
            PositionEntityId.From(Org, PositionId.From("responder")),
            RoutingRejection.Create(RoutingValidationContext.ForMessage(request),
                ValidationResult.Create([RoutingValidationCatalog.PeerChannelRequired()])), At));
        Assert.Null(Assert.Single(audit.ReadByThread(request.Thread)).MessageType);
    }

    private static PeerRequest MediationRequest(string from, string to, ThreadId thread) => new(
        MessageId.New(), Org, new PositionEndpointRef(PositionId.From(from)), new PositionEndpointRef(PositionId.From(to)),
        thread, Priority.Normal, 1, At, null, "Sensitive mediation proposal");

    private static Directive MediationDirective(string from, string to, ThreadId thread) => new(
        MessageId.New(), Org, new PositionEndpointRef(PositionId.From(from)), new PositionEndpointRef(PositionId.From(to)),
        thread, Priority.Normal, 1, At, null, DirectiveId.New(), null, "Implement the negotiated outcome", "Private agreement");

    private static MaterializedOrganizationRelations SiblingRelations() => new(OrganizationRelationsSnapshot
        .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
        .AddPosition(PositionId.From("root-lead"), UnitId.From("root"))
        .AddPosition(PositionId.From("source-lead"), Source, PositionId.From("root-lead"))
        .AddPosition(PositionId.From("destination-lead"), Destination, PositionId.From("root-lead"))
        .AddPosition(PositionId.From("requester"), Source, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("responder"), Destination, PositionId.From("destination-lead"))
        .AddUnitLeadership(Source, PositionId.From("source-lead"))
        .AddUnitLeadership(Destination, PositionId.From("destination-lead")).Build());

    private static IActorRef CreateMediationActor(ActorSystem system, string position, IOrganizationRelations relations,
        IPeerChannelContracts contracts, Registrar registrar, IPositionProjectionPublisher projections) =>
        system.ActorOf(Props.Create(() => new PositionActor(
            PositionEntityId.From(Org, PositionId.From(position)).Value, new Provider(), PositionOccupantFactory.Instance,
            projections, () => At, null, null, null, null, null, null,
            new PeerRequestLimitResolver(relations, contracts), new HorizontalRoutingValidator(relations, contracts), registrar)));

    private sealed class MediationContracts(string policy) : IPeerChannelContracts
    {
        private int _calls;
        public int Calls => Volatile.Read(ref _calls);
        public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(OrganizationId organizationId,
            UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _calls);
            return policy switch
            {
                "absent" => ValueTask.FromResult<PeerChannelConfiguration?>(null),
                "unavailable" => ValueTask.FromException<PeerChannelConfiguration?>(new InvalidOperationException("Registry unavailable")),
                _ => ValueTask.FromResult<PeerChannelConfiguration?>(new PeerChannelConfiguration(fromUnitId,
                    policy == "restricted" ? [PeerChannelMessageType.Memo] : [PeerChannelMessageType.PeerRequest],
                    1, PeerChannelRejectionAction.Escalate)),
            };
        }
    }

    private sealed class MediationAuditLog : IJourneyAuditLog
    {
        private readonly ConcurrentQueue<JourneyAuditRecord> _records = new();
        public void Append(JourneyAuditRecord record) => _records.Enqueue(record);
        public IReadOnlyList<JourneyAuditRecord> ReadByThread(ThreadId threadId, DirectiveId? directiveId = null) =>
            _records.Where(record => record.ThreadId == threadId
                && (directiveId is null || record.DirectiveId == directiveId)).ToArray();
    }
}
