using Akka.Actor;
using Akka.Configuration;
using Akka.Persistence;
using Hive.Actors.Positions;
using Hive.Actors.Serialization;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using static Hive.Tests.PeerResponseRoutingValidatorTests;

namespace Hive.Tests;

public sealed class PeerRequestPersistenceTests
{
    [Fact]
    public void Replay_preserves_requests_across_occupant_processing_and_snapshot()
    {
        var request = Request(deadline: At.AddHours(1));
        var initial = PositionState.Empty.Apply(new PeerRequestRecorded(request, At));
        var delivered = initial.Apply(new MessageReceived(request, At))
            .Apply(new MessageDispatched(request.Id, request.Thread, OccupantId.From("worker"),
                OccupantType.Human, At))
            .Apply(new MessageProcessingCompleted("processing", request.Id, request.Thread,
                MessageProcessingCompletionStatus.Completed, At))
            .Apply(new ShortMemoryUpdated("key", "value", At));
        Assert.Equal(MessageState.Accepted, delivered.PeerRequests[request.Id].State);

        var recovered = PositionState.Restore(delivered.ToSnapshot(At));
        var completed = recovered.Apply(new MessageReceived(Response(request), At))
            .Apply(new PeerRequestRecorded(request, At))
            .Apply(new PeerRequestClosed(request.Id, MessageState.Failed, At));
        Assert.Equal(MessageState.Completed, completed.PeerRequests[request.Id].State);
        Assert.Equal(MessageState.Accepted, initial.PeerRequests[request.Id].State);
        Assert.Equal(MessageState.Completed,
            PositionState.Restore(completed.ToSnapshot(At)).PeerRequests[request.Id].State);
        Assert.Equal([RoutingValidationCatalog.PeerResponseDuplicate()],
            PeerResponseRoutingValidator.Validate(Response(request), completed.PeerRequests[request.Id], At).Errors);
    }

    [Fact]
    public void Invalid_or_late_responses_do_not_close_a_request_and_replay_uses_receipt_time()
    {
        var request = Request(deadline: At.AddTicks(1));
        var state = PositionState.Empty.Apply(new PeerRequestRecorded(request, At));
        foreach (var received in new[]
        {
            new MessageReceived(Response(request, thread: ThreadId.New()), At),
            new MessageReceived(Response(request, from: new PositionEndpointRef(PositionId.From("other"))), At),
            new MessageReceived(Response(request), At.AddTicks(1)),
        })
        {
            Assert.Equal(MessageState.Accepted, state.Apply(received).PeerRequests[request.Id].State);
        }
        Assert.Equal(MessageState.Completed,
            state.Apply(new MessageReceived(Response(request), At)).PeerRequests[request.Id].State);
    }

    [Theory]
    [InlineData(MessageState.Rejected)]
    [InlineData(MessageState.Failed)]
    public void Closed_request_survives_snapshot_and_cannot_be_reopened(MessageState terminal)
    {
        var request = Request();
        var state = PositionState.Empty.Apply(new PeerRequestRecorded(request, At))
            .Apply(new PeerRequestClosed(request.Id, terminal, At));
        state = PositionState.Restore(state.ToSnapshot(At))
            .Apply(new PeerRequestRecorded(request, At))
            .Apply(new MessageReceived(Response(request), At));
        Assert.Equal(terminal, state.PeerRequests[request.Id].State);
    }

    [Fact]
    public void Snapshot_materializes_collection_and_rejects_duplicate_ids()
    {
        var record = new PeerRequestRecord(Request(), MessageState.Accepted);
        var records = new List<PeerRequestRecord> { record };
        var snapshot = new PositionSnapshot(At, peerRequests: records);
        records.Clear();
        Assert.Equal(record, Assert.Single(snapshot.PeerRequests));
        Assert.Throws<ArgumentException>(() => new PositionSnapshot(At, peerRequests: [record, record]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ClosePeerRequest(record.Request.Id, MessageState.Completed));
    }

    [Fact]
    public void New_protocol_values_and_snapshot_round_trip_and_old_snapshot_defaults_to_empty()
    {
        var request = Request(deadline: At.AddHours(1));
        object[] values = [
            new RecordPeerRequest(request), new ClosePeerRequest(request.Id, MessageState.Failed),
            new FindPeerRequest(request.Id), new PeerRequestLookupResult(null),
            new PeerRequestLookupResult(new PeerRequestRecord(request, MessageState.Completed)),
            new PeerRequestRecorded(request, At), new PeerRequestClosed(request.Id, MessageState.Rejected, At),
            PositionEnvelope.For(Entity(request), new FindPeerRequest(request.Id)),
            new PositionSnapshot(At, peerRequests: [new PeerRequestRecord(request, MessageState.Completed)]),
        ];
        foreach (var value in values)
        {
            var bytes = PositionProtocolJsonFormat.Serialize(value);
            var restored = PositionProtocolJsonFormat.Deserialize(PositionProtocolManifests.ForType(value.GetType()), bytes);
            Assert.Equal(bytes, PositionProtocolJsonFormat.Serialize(restored));
        }
        var old = PositionProtocolJsonFormat.Serialize(new PositionSnapshot(At));
        Assert.DoesNotContain("PeerRequests", System.Text.Encoding.UTF8.GetString(old));
        Assert.Empty(((PositionSnapshot)PositionProtocolJsonFormat.Deserialize("position-snapshot", old)).PeerRequests);
    }

    [Fact]
    public async Task Actor_records_idempotently_queries_and_recovers_snapshot_plus_response_journal()
    {
        var request = Request(deadline: At.AddHours(1));
        var entity = Entity(request);
        using var system = ActorSystem.Create("peer-persistence-" + Guid.NewGuid().ToString("N"),
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
            var actor = CreateActor(system, entity);
            var recorded = await actor.Ask<PeerRequestLookupResult>(new RecordPeerRequest(request), Timeout);
            Assert.Equal(MessageState.Accepted, recorded.Record!.State);
            Assert.Equal(recorded, await actor.Ask<PeerRequestLookupResult>(new RecordPeerRequest(request), Timeout));
            Assert.Null((await actor.Ask<PeerRequestLookupResult>(new FindPeerRequest(MessageId.New()), Timeout)).Record);
            var conflicting = Request(id: request.Id);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                actor.Ask<PeerRequestLookupResult>(new RecordPeerRequest(conflicting), Timeout));
            var otherEntityRequest = Request(organization: OrganizationId.From("other"));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                actor.Ask<PeerRequestLookupResult>(new RecordPeerRequest(otherEntityRequest), Timeout));
            var wrongRequester = CreateActor(system,
                PositionEntityId.From(entity.Organization, PositionId.From("other")));
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                wrongRequester.Ask<PeerRequestLookupResult>(new RecordPeerRequest(request), Timeout));

            var snapshot = (await actor.Ask<PositionState>(GetPositionState.Instance, Timeout)).ToSnapshot(At);
            await actor.GracefulStop(Timeout);
            var seeder = system.ActorOf(Props.Create(() => new SnapshotSeeder(
                PositionActor.PersistenceIdFor(entity.Value))));
            await seeder.Ask<bool>(snapshot, Timeout);
            await seeder.GracefulStop(Timeout);

            actor = CreateActor(system, entity);
            Assert.Equal(recorded, await actor.Ask<PeerRequestLookupResult>(new FindPeerRequest(request.Id), Timeout));
            await actor.Ask<AcceptMessageResult>(new AcceptMessage(Response(request)), Timeout);
            await actor.GracefulStop(Timeout);
            actor = CreateActor(system, entity);
            var recovered = await actor.Ask<PeerRequestLookupResult>(new FindPeerRequest(request.Id), Timeout);
            Assert.Equal(MessageState.Completed, recovered.Record!.State);
            Assert.Equal(recovered, await actor.Ask<PeerRequestLookupResult>(new RecordPeerRequest(request), Timeout));

            var open = Request();
            await actor.Ask<PeerRequestLookupResult>(new RecordPeerRequest(open), Timeout);
            await actor.Ask<PeerRequestLookupResult>(new ClosePeerRequest(open.Id, MessageState.Failed), Timeout);
            await actor.GracefulStop(Timeout);
            actor = CreateActor(system, entity);
            Assert.Equal(MessageState.Failed,
                (await actor.Ask<PeerRequestLookupResult>(new FindPeerRequest(open.Id), Timeout)).Record!.State);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Adapter_routes_by_organization_and_requester_and_propagates_cancellation_and_timeout()
    {
        using var system = ActorSystem.Create("peer-log-" + Guid.NewGuid().ToString("N"));
        try
        {
            var request = Request();
            var region = system.ActorOf(Props.Create(() => new QueryRegion(request)));
            var log = new PositionPeerRequestLog(region, TimeSpan.FromMilliseconds(500));
            var entity = Entity(request);
            Assert.Equal(request, (await log.FindRequestAsync(entity.Organization, entity.Position, request.Id))!.Request);
            Assert.Null(await log.FindRequestAsync(OrganizationId.From("other"), entity.Position, request.Id));
            Assert.Null(await log.FindRequestAsync(entity.Organization, PositionId.From("other"), request.Id));
            Assert.Null(await log.FindRequestAsync(entity.Organization, entity.Position, MessageId.New()));
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                log.FindRequestAsync(entity.Organization, entity.Position, request.Id, canceled.Token).AsTask());
            var unavailable = new PositionPeerRequestLog(system.DeadLetters, TimeSpan.FromMilliseconds(50));
            await Assert.ThrowsAsync<AskTimeoutException>(() =>
                unavailable.FindRequestAsync(entity.Organization, entity.Position, request.Id).AsTask());
            using var pendingCancellation = new CancellationTokenSource();
            var pending = new PositionPeerRequestLog(system.DeadLetters, TimeSpan.FromSeconds(10))
                .FindRequestAsync(entity.Organization, entity.Position, request.Id, pendingCancellation.Token).AsTask();
            pendingCancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        }
        finally { await system.Terminate(); }
    }

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);
    private static PositionEntityId Entity(PeerRequest request) => PositionEntityId.From(
        request.OrganizationId, ((PositionEndpointRef)request.From).PositionId);
    private static IActorRef CreateActor(ActorSystem system, PositionEntityId entity) => system.ActorOf(
        Props.Create(() => new PositionActor(entity.Value, new Provider(), () => At)));

    private sealed class Provider : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(PositionEntityId entity,
            CancellationToken cancellationToken) => Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(
            new PositionRuntimeConfiguration(new PositionConfigurationStamp(1, "sha256:peer-test"),
                entity.Organization, entity.Position,
                new PositionRuntimeDescriptor(UnitId.From("unit"), null, entity.Position.Value, "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(OccupantType.Human), new PositionAuthorityRuntimeConfiguration([]))));
    }

    private sealed class SnapshotSeeder : ReceivePersistentActor
    {
        private IActorRef _replyTo = ActorRefs.Nobody;
        public SnapshotSeeder(string persistenceId)
        {
            PersistenceId = persistenceId;
            Recover<PositionEvent>(_ => { });
            Command<PositionSnapshot>(snapshot => { _replyTo = Sender; SaveSnapshot(snapshot); });
            Command<SaveSnapshotSuccess>(_ => _replyTo.Tell(true));
            Command<SaveSnapshotFailure>(failure => _replyTo.Tell(new Status.Failure(failure.Cause)));
        }
        public override string PersistenceId { get; }
    }

    private sealed class QueryRegion : ReceiveActor
    {
        public QueryRegion(PeerRequest request)
        {
            Receive<PositionEnvelope>(envelope => Sender.Tell(new PeerRequestLookupResult(
                envelope.Position == Entity(request) && envelope.Command is FindPeerRequest query && query.RequestId == request.Id
                    ? new PeerRequestRecord(request, MessageState.Accepted) : null)));
        }
    }
}
