using System.Collections.Concurrent;
using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Actors.Sharding;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;
using Directive = Hive.Domain.Messaging.Directive;

namespace Hive.Tests.PostgreSql;

[Collection(AkkaPostgreSqlCollection.Name)]
public sealed class PostgreSqlPeerChannelIntegrationTests(PostgreSqlFixture database)
{
    private static readonly OrganizationId Org = OrganizationId.From("peer-integration");
    private static readonly UnitId Source = UnitId.From("source");
    private static readonly UnitId Destination = UnitId.From("destination");
    private static readonly DateTimeOffset At = new(2026, 9, 12, 10, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData("passivation")]
    [InlineData("restart")]
    [InlineData("rebalance")]
    public async Task Open_capacity_and_correlation_survive_relocation_and_response_releases_capacity(string transition)
    {
        await database.ResetPersistenceAsync();
        // Each side covers every shard, so a real rebalance relocates both kinds of durable state.
        var requesters = PositionShardingMultiNodeFixture.GenerateEntitiesCoveringShards(Org, "source", 8).ToArray();
        var recipients = PositionShardingMultiNodeFixture.GenerateEntitiesCoveringShards(Org, "destination", 8).ToArray();
        var entities = requesters.Concat(recipients).ToArray();
        var topology = Relations(requesters, recipients);
        var audit = new AuditLog();
        await using var cluster = await Start(topology, Channels(), audit, startAllRegions: transition == "restart");
        var requests = requesters.Zip(recipients, (from, to) => Request(from.Position, to.Position)).ToArray();
        foreach (var entity in entities)
            await cluster.ChangeOccupantAsync(entity, OccupantId.From("fixture-agent"), OccupantType.AiAgent);
        foreach (var request in requests)
        {
            Assert.True((await Accept(cluster, request)).IsAccepted);
            await WaitForRequest(cluster, request, MessageState.Accepted);
            await cluster.WaitForMessageProcessingCompletedAsync(Entity(request.To), request.Id);
        }
        var before = await cluster.WaitForAllEntitiesLocatedAsync(entities);
        if (transition != "restart")
            Assert.All(before.Where(location => location.EntityIds.Count > 0), location => Assert.Equal("agents-1", location.NodeName));

        if (transition == "passivation")
        {
            foreach (var entity in entities)
            {
                await cluster.PassivateAsync(entity, "peer-integration");
                await cluster.WaitForEntityInactiveAsync(entity);
            }
            await cluster.ActivateAsync(entities);
            foreach (var entity in entities)
                await Eventually(() => Task.FromResult(cluster.ProjectionEvents<PositionReactivated>(entity).Count > 0));
        }
        else if (transition == "restart")
        {
            var owner = before.First(location => location.EntityIds.Any(id => requesters.Any(entity => entity.Value == id)));
            Assert.Contains(recipients, entity => owner.EntityIds.Contains(entity.Value));
            var original = cluster.AgentNodes.Single(node => node.Name == owner.NodeName).System;
            await cluster.RestartAgentNodeAsync(owner.NodeName);
            Assert.True(original.WhenTerminated.IsCompleted);
            Assert.NotSame(original, cluster.AgentNodes.Single(node => node.Name == owner.NodeName).System);
            await cluster.ActivateAsync(entities);
        }
        else
        {
            await cluster.StartRemainingAgentShardRegionsAsync();
            var after = await cluster.WaitForRebalancedLocationsAsync(entities);
            var moved = after.Single(location => location.NodeName == "agents-2").EntityIds;
            Assert.Contains(requesters, entity => moved.Contains(entity.Value));
            Assert.Contains(recipients, entity => moved.Contains(entity.Value));
        }

        foreach (var request in requests)
        {
            await WaitForRequest(cluster, request, MessageState.Accepted);
            Assert.Equal(AcceptMessageDecision.AlreadyAccepted, (await Accept(cluster, request)).Decision);
            var retry = Request(((PositionEndpointRef)request.From).PositionId, ((PositionEndpointRef)request.To).PositionId);
            Assert.Equal(RejectionReason.LimitExceeded, (await Accept(cluster, retry)).Reason);
            Assert.DoesNotContain(cluster.CommittedEvents<MessageReceived>(Entity(retry.To)), item => item.Event.Message.Id == retry.Id);
            var escalationId = new RecordPeerRequestRejection(retry, RejectionReason.LimitExceeded).EscalationId;
            await Eventually(() => Task.FromResult(cluster.CommittedEvents<MessageReceived>(Entity("source-lead"))
                .Any(item => item.Event.Message.Id == escalationId && item.Event.Message.Thread == retry.Thread)));

            var response = await Reply(cluster, request);
            await WaitForRequest(cluster, request, MessageState.Completed);
            Assert.Equal(request.Thread, response.Thread);
            Assert.Equal(request.Id, response.InReplyTo);
            Assert.True((await Accept(cluster, retry)).IsAccepted);
            await WaitForRequest(cluster, retry, MessageState.Accepted);
            Assert.Equal(RejectionReason.Duplicate, (await Accept(cluster, Response(request))).Reason);
            Assert.Equal(AcceptMessageDecision.AlreadyAccepted, (await Accept(cluster, response)).Decision);
            Assert.Single(cluster.CommittedEvents<MessageReceived>(Entity(request.To)), item => item.Event.Message.Id == request.Id);
            Assert.Single(cluster.CommittedEvents<MessageReceived>(Entity(response.To)), item => item.Event.Message.Id == response.Id);
            AssertAudit(audit, retry, "peer-channel-limit-exceeded");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Out_of_contract_requests_are_audited_and_only_declared_escalation_reaches_superior(bool declared)
    {
        await database.ResetPersistenceAsync();
        var requester = Entity("source-worker");
        var recipient = Entity("destination-worker");
        var audit = new AuditLog();
        await using var cluster = await Start(Relations([requester], [recipient]), Channels(declared, memoOnly: true), audit);
        var request = Request(requester.Position, recipient.Position);
        Assert.Equal(RejectionReason.InvalidRoute, (await Accept(cluster, request)).Reason);
        AssertAudit(audit, request, declared ? "peer-type-not-allowed" : "peer-channel-required");
        Assert.Empty(cluster.CommittedEvents<MessageReceived>(recipient));
        var notification = new RecordPeerRequestRejection(request, RejectionReason.InvalidRoute);
        if (declared)
        {
            await Eventually(() => Task.FromResult(cluster.CommittedEvents<MessageReceived>(Entity("source-lead"))
                .Any(item => item.Event.Message.Id == notification.EscalationId)));
            var escalation = Assert.IsType<Escalation>(Assert.Single(cluster.CommittedEvents<MessageReceived>(Entity("source-lead"))).Event.Message);
            Assert.Equal(request.Thread, escalation.Thread);
            Assert.Equal(request.From, escalation.From);
            Assert.Equal(request.Priority, escalation.Priority);
            Assert.Contains("invalid-route", escalation.Context);
            Assert.DoesNotContain(request.Ask, escalation.Context);
            // The real sharded notification/emission chain acknowledges both durable outboxes.
            foreach (var entity in new[] { requester, recipient })
                await Eventually(() => Task.FromResult(cluster.CommittedEvents<PeerRejectionEscalationUpdated>(entity)
                    .Any(item => item.Event.Completed)));
            await cluster.RestartAgentNodeAsync("agents-1");
            Assert.Equal(RejectionReason.InvalidRoute, (await Accept(cluster, request)).Reason);
            var another = Request(requester.Position, recipient.Position, request.Thread);
            Assert.Equal(RejectionReason.InvalidRoute, (await Accept(cluster, another)).Reason);
            Assert.Single(cluster.CommittedEvents<MessageReceived>(Entity("source-lead")));
        }
        else
        {
            Assert.Empty(cluster.CommittedEvents<PeerRejectionEscalationUpdated>(recipient));
            Assert.Empty(cluster.CommittedEvents<MessageReceived>(Entity("source-lead")));
        }
    }

    [Fact]
    public async Task Leadership_mediation_round_trip_precedes_validated_local_directives()
    {
        await database.ResetPersistenceAsync();
        var requester = Entity("source-worker");
        var recipient = Entity("destination-worker");
        var relations = Relations([requester], [recipient]);
        var audit = new AuditLog();
        await using var cluster = await Start(relations, Channels(declared: false), audit);
        var thread = ThreadId.New();
        foreach (var (from, to) in new[] { ("source-lead", "destination-lead"), ("destination-lead", "source-lead") })
        {
            var request = Request(PositionId.From(from), PositionId.From(to), thread);
            Assert.True((await Accept(cluster, request)).IsAccepted);
            await WaitForRequest(cluster, request, MessageState.Accepted);
            var response = await Reply(cluster, request);
            await WaitForRequest(cluster, request, MessageState.Completed);
            Assert.Equal(thread, response.Thread);
            Assert.Null(Assert.Single(cluster.CommittedEvents<MessageReceived>(Entity(to)), item => item.Event.Message.Id == request.Id).Event.PeerChannel);
        }
        Assert.Empty(cluster.CommittedEvents<MessageReceived>(requester));
        Assert.Empty(cluster.CommittedEvents<MessageReceived>(recipient));
        var validator = new DirectiveRoutingValidator(relations);
        foreach (var (leader, local, other) in new[] { ("source-lead", requester, recipient), ("destination-lead", recipient, requester) })
        {
            var directive = NewDirective(leader, local.Position, thread);
            Assert.True((await validator.ValidateAsync(directive)).IsValid);
            Assert.True((await Accept(cluster, directive)).IsAccepted);
            var cross = await validator.ValidateAsync(NewDirective(leader, other.Position, thread));
            Assert.Equal("direct-subordinate-required", Assert.Single(cross.Errors).Code);
            Assert.Equal(thread, Assert.Single(cluster.CommittedEvents<MessageReceived>(local)).Event.Message.Thread);
        }
        Assert.Equal(6, audit.ReadByThread(thread).Count(record => record.Stage == JourneyAuditStage.PositionAccepted));
        Assert.Empty(cluster.CommittedEvents<MessageReceived>(Entity("root-lead")));
    }

    private Task<PositionShardingMultiNodeFixture> Start(IOrganizationRelations relations, IPeerChannelContracts channels,
        AuditLog audit, bool startAllRegions = true) =>
        PositionShardingMultiNodeFixture.StartAsync(startAllRegions, database.ConnectionString, (node, id) =>
            Props.Create(() => new PositionActor(id, node.ConfigurationProvider, PositionOccupantFactory.Instance,
                new JourneyAuditPositionProjectionPublisher(audit, node.Publisher), () => At, null,
                new OccupantReplyMessageValidator(relations), null, null, null, null,
                new PeerRequestLimitResolver(relations, channels), new HorizontalRoutingValidator(relations, channels), null)));

    private static Task<AcceptMessageResult> Accept(PositionShardingMultiNodeFixture cluster, OrgMessage message) =>
        Ask<AcceptMessageResult>(cluster, Entity(message.To), new AcceptMessage(message));

    private static Task<T> Ask<T>(PositionShardingMultiNodeFixture cluster, PositionEntityId entity, PositionCommand command) =>
        cluster.AgentNodes.First(node => node.Region is not null).Region!.Ask<T>(PositionEnvelope.For(entity, command), Timeout);

    private static async Task<PeerResponse> Reply(PositionShardingMultiNodeFixture cluster, PeerRequest request)
    {
        var result = await Ask<OccupantReplyEmissionResult>(cluster, Entity(request.To), new EmitOccupantReply(
            request.Id, MessageId.New(), OccupantReplyAuthor.HumanUser("reviewer", "web-inbox"), "Reviewed evidence."));
        return Assert.IsType<PeerResponse>(result.Message);
    }

    private static Task WaitForRequest(PositionShardingMultiNodeFixture cluster, PeerRequest request, MessageState state) =>
        Eventually(async () => (await new PositionPeerRequestLog(cluster.AgentNodes.First(node => node.Region is not null).System)
            .FindRequestAsync(Org, ((PositionEndpointRef)request.From).PositionId, request.Id))?.State == state);

    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        var deadline = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await predicate()) return;
            await Task.Delay(100);
        }
        Assert.Fail("Expected peer integration condition was not observed before the deadline.");
    }

    private static void AssertAudit(AuditLog audit, PeerRequest request, string reason)
    {
        var record = Assert.Single(audit.ReadByThread(request.Thread).Where(item => item.MessageId == request.Id
            && item.Outcome == JourneyAuditOutcome.Rejected));
        Assert.Equal(reason, record.ReasonCode);
        Assert.Equal(Org, record.OrganizationId);
        Assert.Equal(Entity(request.To).Position, record.PositionId);
        Assert.Equal(nameof(PeerRequest), record.MessageType);
        Assert.DoesNotContain(request.Ask, string.Join("|", record.Payload.Values));
    }

    private static MaterializedOrganizationRelations Relations(PositionEntityId[] requesters, PositionEntityId[] recipients)
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(Org, new OrganizationOwnerEndpointRef())
            .AddPosition(PositionId.From("root-lead"), UnitId.From("root"))
            .AddPosition(PositionId.From("source-lead"), Source, PositionId.From("root-lead"))
            .AddPosition(PositionId.From("destination-lead"), Destination, PositionId.From("root-lead"))
            .AddUnitLeadership(Source, PositionId.From("source-lead"))
            .AddUnitLeadership(Destination, PositionId.From("destination-lead"));
        foreach (var entity in requesters) builder.AddPosition(entity.Position, Source, PositionId.From("source-lead"));
        foreach (var entity in recipients) builder.AddPosition(entity.Position, Destination, PositionId.From("destination-lead"));
        return new MaterializedOrganizationRelations(builder.Build());
    }

    private static MaterializedPeerChannelContracts Channels(bool declared = true, bool memoOnly = false)
    {
        var channel = new PeerChannelConfiguration(Source,
            memoOnly ? [PeerChannelMessageType.Memo] : [PeerChannelMessageType.PeerRequest], 1, PeerChannelRejectionAction.Escalate);
        var builder = PeerChannelContractsSnapshot.CreateBuilder(Org).AddUnit(Source, [])
            .AddUnit(Destination, declared ? [channel] : []).AddUnit(UnitId.From("root"), []);
        return new MaterializedPeerChannelContracts(builder.Build());
    }

    private static PositionEntityId Entity(string position) => PositionEntityId.From(Org, PositionId.From(position));
    private static PositionEntityId Entity(EndpointRef endpoint) => PositionEntityId.From(Org, ((PositionEndpointRef)endpoint).PositionId);
    private static PeerRequest Request(PositionId from, PositionId to, ThreadId? thread = null) => new(
        MessageId.New(), Org, new PositionEndpointRef(from), new PositionEndpointRef(to), thread ?? ThreadId.New(),
        Priority.High, 1, At, null, "Confidential peer evidence request.");
    private static PeerResponse Response(PeerRequest request) => new(MessageId.New(), Org, request.To, request.From,
        request.Thread, request.Priority, 1, At, null, request.Id, "Duplicate response.");
    private static Directive NewDirective(string leader, PositionId worker, ThreadId thread) => new(MessageId.New(), Org,
        new PositionEndpointRef(PositionId.From(leader)), new PositionEndpointRef(worker), thread, Priority.Normal, 1, At, null,
        DirectiveId.New(), null, "Apply the agreement", "Mediated outcome");

    private sealed class AuditLog : IJourneyAuditLog
    {
        private readonly ConcurrentQueue<JourneyAuditRecord> _records = new();
        public void Append(JourneyAuditRecord record) => _records.Enqueue(record);
        public IReadOnlyList<JourneyAuditRecord> ReadByThread(ThreadId threadId, DirectiveId? directiveId = null) =>
            _records.Where(record => record.ThreadId == threadId && (directiveId is null || record.DirectiveId == directiveId)).ToArray();
    }
}
