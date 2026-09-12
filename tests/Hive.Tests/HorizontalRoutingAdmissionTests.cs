using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Configuration;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using static Hive.Tests.PeerResponseRoutingValidatorTests;

namespace Hive.Tests;

public sealed partial class HorizontalRoutingAdmissionTests
{
    private static readonly OrganizationId Org = Request().OrganizationId;
    private static readonly UnitId Source = UnitId.From("source");
    private static readonly UnitId Destination = UnitId.From("destination");
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Request_and_memo_dispatch_to_horizontal_validator_and_sanitize_rejections(bool memo)
    {
        var request = Request();
        OrgMessage message = memo ? Memo(request) : request;
        var contracts = new Contracts { Allowed = false };
        var gate = Gate(contracts);
        var rejection = (await gate.AdmitAsync(message)).Rejection!;
        Assert.Equal("peer-channel-required", Assert.Single(rejection.AuditResult.Errors).Code);
        Assert.Equal(new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute),
            Assert.Single(rejection.PublicResult.Errors));
        Assert.Equal(message.Id, rejection.Context.MessageId);
        Assert.Equal(message.Thread, rejection.Context.Thread);
        contracts.Allowed = true;
        Assert.True((await gate.AdmitAsync(message)).IsAdmitted);
        contracts.Types = [memo ? PeerChannelMessageType.PeerRequest : PeerChannelMessageType.Memo];
        Assert.Equal("peer-type-not-allowed",
            Assert.Single((await gate.AdmitAsync(message)).Rejection!.AuditResult.Errors).Code);
    }

    [Theory]
    [InlineData(null, "peer-request-not-found", RejectionReason.InvalidRoute)]
    [InlineData(MessageState.Completed, "peer-response-duplicate", RejectionReason.Duplicate)]
    [InlineData(MessageState.Failed, "peer-request-not-open", RejectionReason.InvalidRoute)]
    [InlineData(MessageState.Rejected, "peer-request-not-open", RejectionReason.InvalidRoute)]
    [InlineData(MessageState.Accepted, "peer-request-expired", RejectionReason.Expired)]
    public async Task Response_dispatch_uses_original_request_and_preserves_public_reason(
        MessageState? state, string code, RejectionReason reason)
    {
        var request = Request(deadline: At);
        var gate = Gate(new Contracts { Failure = new InvalidOperationException("must not query") },
            new Log(state is { } value ? new PeerRequestRecord(request, value) : null));
        var rejection = (await gate.AdmitAsync(Response(request))).Rejection!;
        Assert.Equal(code, Assert.Single(rejection.AuditResult.Errors).Code);
        Assert.Equal(new ValidationError(RejectionReasonContract.ToWireValue(reason), "$", reason),
            Assert.Single(rejection.PublicResult.Errors));
    }

    [Fact]
    public async Task Response_admission_ignores_current_channel_and_propagates_log_failures()
    {
        var request = Request();
        var log = new Log(new PeerRequestRecord(request, MessageState.Accepted));
        var gate = Gate(new Contracts { Failure = new InvalidOperationException("must not query") }, log);
        Assert.True((await gate.AdmitAsync(Response(request))).IsAdmitted);
        log.Failure = new InvalidOperationException("requester unavailable");
        Assert.Same(log.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            gate.AdmitAsync(Response(request)).AsTask()));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            gate.AdmitAsync(Response(request), cancellation.Token).AsTask());
    }

    [Fact]
    public async Task Actor_rejects_routes_before_capacity_audits_and_allows_retry_after_policy_change()
    {
        using var system = System();
        try
        {
            var contracts = new Contracts { Allowed = false };
            var registrar = new Registrar();
            var projections = new Projections();
            var actor = Create(system, "responder", contracts, registrar, projections);
            var request = Request();
            var rejected = await actor.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout);
            Assert.Equal(RejectionReason.InvalidRoute, rejected.Reason);
            var rejection = Assert.Single(projections.Events.OfType<PositionMessageRoutingRejected>()).Rejection;
            Assert.Equal("peer-channel-required", Assert.Single(rejection.AuditResult.Errors).Code);
            Assert.Empty((await State(actor)).ProcessedMessages);
            Assert.Empty(registrar.Requests);
            contracts.Allowed = true;
            registrar.Actors["requester"] = Create(system, "requester", contracts, registrar, projections);
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)).IsAccepted);
            await Eventually(async () => (await State(registrar.Actors["requester"])).PeerRequests.ContainsKey(request.Id));
            contracts.Failure = new InvalidOperationException("registry offline");
            Assert.Equal(AcceptMessageDecision.AlreadyAccepted,
                (await actor.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)).Decision);
            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                actor.Ask<AcceptMessageResult>(new AcceptMessage(Request()), Timeout));
            Assert.Single(projections.Events.OfType<PositionMessageRoutingRejected>());
            Assert.Single((await State(actor)).ProcessedMessages);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Automatic_registration_and_concurrent_responses_survive_recovery_without_reopening_request()
    {
        using var system = System();
        try
        {
            var contracts = new Contracts();
            var registrar = new Registrar();
            var projections = new Projections();
            var requester = Create(system, "requester", contracts, registrar, projections);
            registrar.Actors["requester"] = requester;
            var responder = Create(system, "responder", contracts, registrar, projections);
            var request = Request();
            Assert.True((await responder.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)).IsAccepted);
            await Eventually(async () => (await State(requester)).PeerRequests.ContainsKey(request.Id));
            var replies = Enumerable.Range(0, 8).Select(_ => Response(request)).ToArray();
            var results = await Task.WhenAll(replies.Select(reply =>
                requester.Ask<AcceptMessageResult>(new AcceptMessage(reply), Timeout)));
            Assert.Single(results.Where(result => result.IsAccepted));
            Assert.Equal(7, results.Count(result => result.Reason == RejectionReason.Duplicate));
            Assert.Equal(7, projections.Events.OfType<PositionMessageRoutingRejected>().Count());
            var accepted = replies.Single(reply => results.Single(result => result.MessageId == reply.Id).IsAccepted);
            await requester.GracefulStop(Timeout);
            requester = Create(system, "requester", contracts, registrar, projections);
            registrar.Actors["requester"] = requester;
            contracts.Failure = new InvalidOperationException("current channels unavailable");
            Assert.Equal(AcceptMessageDecision.AlreadyAccepted,
                (await requester.Ask<AcceptMessageResult>(new AcceptMessage(accepted), Timeout)).Decision);
            Assert.Equal(RejectionReason.Duplicate,
                (await requester.Ask<AcceptMessageResult>(new AcceptMessage(Response(request)), Timeout)).Reason);
            Assert.Single((await State(requester)).ProcessedMessages);
            await responder.GracefulStop(Timeout);
            _ = Create(system, "responder", contracts, registrar, projections);
            await Eventually(() => Task.FromResult(registrar.Requests.Count >= 2));
            Assert.Equal(MessageState.Completed, (await State(requester)).PeerRequests[request.Id].State);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Registration_recovers_after_failure_and_reciprocal_requests_do_not_deadlock()
    {
        using var system = System();
        try
        {
            var contracts = new Contracts();
            var registrar = new Registrar { Unavailable = true };
            var projections = new Projections();
            registrar.Actors["requester"] = Create(system, "requester", contracts, registrar, projections);
            registrar.Actors["responder"] = Create(system, "responder", contracts, registrar, projections);
            var forward = Request();
            var reverse = new PeerRequest(MessageId.New(), Org, forward.To, forward.From, ThreadId.New(),
                Priority.Normal, 1, At, null, "Review the other change");
            Assert.All(await Task.WhenAll(
                registrar.Actors["responder"].Ask<AcceptMessageResult>(new AcceptMessage(forward), Timeout),
                registrar.Actors["requester"].Ask<AcceptMessageResult>(new AcceptMessage(reverse), Timeout)),
                result => Assert.True(result.IsAccepted));
            Assert.Empty((await State(registrar.Actors["requester"])).PeerRequests);
            await registrar.Actors["responder"].GracefulStop(Timeout);
            registrar.Unavailable = false;
            registrar.Actors["responder"] = Create(system, "responder", contracts, registrar, projections);
            await Eventually(async () => (await State(registrar.Actors["requester"])).PeerRequests.ContainsKey(forward.Id)
                && (await State(registrar.Actors["responder"])).PeerRequests.ContainsKey(reverse.Id));
            Assert.True((await registrar.Actors["requester"].Ask<AcceptMessageResult>(
                new AcceptMessage(Response(forward)), Timeout)).IsAccepted);
            Assert.True((await registrar.Actors["responder"].Ask<AcceptMessageResult>(
                new AcceptMessage(Response(reverse)), Timeout)).IsAccepted);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Self_request_records_without_self_ask_deadlock_and_orphans_never_enter_inbox()
    {
        using var system = System();
        try
        {
            var actor = Create(system, "requester", new Contracts(), new Registrar(), new Projections());
            Assert.Equal(RejectionReason.InvalidRoute,
                (await actor.Ask<AcceptMessageResult>(new AcceptMessage(Response(Request())), Timeout)).Reason);
            Assert.Empty((await State(actor)).Inbox);
            var request = new PeerRequest(MessageId.New(), Org, Request().From, Request().From, ThreadId.New(),
                Priority.Normal, 1, At, null, "Local request");
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)).IsAccepted);
            await Eventually(async () => (await State(actor)).PeerRequests.ContainsKey(request.Id));
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(Response(request)), Timeout)).IsAccepted);
        }
        finally { await system.Terminate(); }
    }

    private static RoutingAdmissionValidator Gate(Contracts contracts, Log? log = null) => new(
        new HorizontalRoutingValidator(Relations(), contracts), new PeerResponseRoutingValidator(log ?? new Log(null), new Clock()));

    [Fact]
    public async Task Occupant_delivery_waits_for_durable_requester_registration()
    {
        using var system = System();
        try
        {
            var contracts = new Contracts();
            var registrar = new Registrar { Barrier = new(TaskCreationOptions.RunContinuationsAsynchronously) };
            var projections = new Projections();
            registrar.Actors["requester"] = Create(system, "requester", contracts, registrar, projections);
            var capture = new CaptureFactory();
            var actor = system.ActorOf(Props.Create(() => new PositionActor(
                PositionEntityId.From(Org, PositionId.From("responder")).Value, new Provider(), capture,
                projections, () => At, null, null, null, null, null, null,
                new PeerRequestLimitResolver(Relations(), contracts), new HorizontalRoutingValidator(Relations(), contracts), registrar)));
            actor.Tell(new ChangeOccupant(OccupantId.From("agent"), OccupantType.AiAgent));
            var request = Request();
            Assert.True((await actor.Ask<AcceptMessageResult>(new AcceptMessage(request), Timeout)).IsAccepted);
            await Eventually(() => Task.FromResult(registrar.Requests.Contains(request)));
            Assert.False(capture.Delivered.Task.IsCompleted);
            Assert.Empty((await State(registrar.Actors["requester"])).PeerRequests);
            registrar.Barrier.SetResult(true);
            Assert.Equal(request, await capture.Delivered.Task.WaitAsync(Timeout));
            Assert.Equal(request, (await State(registrar.Actors["requester"])).PeerRequests[request.Id].Request);
        }
        finally { await system.Terminate(); }
    }

    [Fact]
    public async Task Occupant_response_emission_consults_durable_requester_log_and_sanitizes_failure()
    {
        var request = Request();
        var log = new Log(null);
        var validator = new OccupantReplyMessageValidator(Relations(), new Clock()).WithPeerRequestLog(log);
        var state = PositionState.Empty.Apply(new MessageReceived(request, At));
        Assert.Equal(new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute),
            Assert.Single((await validator.ValidateAsync(state, Response(request))).Errors));
        log.Failure = new InvalidOperationException("log unavailable");
        Assert.Same(log.Failure, await Assert.ThrowsAsync<InvalidOperationException>(() =>
            validator.ValidateAsync(state, Response(request)).AsTask()));
        validator = new OccupantReplyMessageValidator(Relations(), new Clock()).WithPeerRequestLog(
            new Log(new PeerRequestRecord(request, MessageState.Accepted)));
        Assert.True((await validator.ValidateAsync(state, Response(request))).IsValid);
    }

    private static Memo Memo(PeerRequest request) => new(MessageId.New(), Org, request.From, request.To,
        request.Thread, Priority.Normal, 1, At, null, "FYI");

    private static MaterializedOrganizationRelations Relations() => new(OrganizationRelationsSnapshot
        .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
        .AddPosition(PositionId.From("source-lead"), Source)
        .AddPosition(PositionId.From("requester"), Source, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("destination-lead"), Destination, PositionId.From("source-lead"))
        .AddPosition(PositionId.From("responder"), Destination, PositionId.From("destination-lead"))
        .AddUnitLeadership(Destination, PositionId.From("destination-lead")).Build());

    private static ActorSystem System() => ActorSystem.Create("horizontal-" + Guid.NewGuid().ToString("N"),
        ConfigurationFactory.ParseString("""
            akka.persistence.journal.plugin = "akka.persistence.journal.inmem"
            akka.persistence.snapshot-store.plugin = "akka.persistence.snapshot-store.inmem"
            """));

    private static IActorRef Create(ActorSystem system, string position, Contracts contracts,
        Registrar registrar, Projections projections, IPositionMessageEmitter? emitter = null) => system.ActorOf(Props.Create(() => new PositionActor(
            PositionEntityId.From(Org, PositionId.From(position)).Value, new Provider(), PositionOccupantFactory.Instance,
            projections, () => At, null, null, emitter, null, null, null,
            new PeerRequestLimitResolver(Relations(), contracts), new HorizontalRoutingValidator(Relations(), contracts), registrar)));

    private static Task<PositionState> State(IActorRef actor) => actor.Ask<PositionState>(GetPositionState.Instance, Timeout);

    private static async Task Eventually(Func<Task<bool>> predicate)
    {
        var until = DateTime.UtcNow + Timeout;
        while (DateTime.UtcNow < until)
        {
            if (await predicate()) return;
            await Task.Delay(20);
        }
        Assert.Fail("The durable peer request continuation did not complete.");
    }

    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => At; }

    private sealed class Log(PeerRequestRecord? record) : IPeerRequestLog
    {
        public Exception? Failure { get; set; }
        public ValueTask<PeerRequestRecord?> FindRequestAsync(OrganizationId organizationId, PositionId requester,
            MessageId requestId, CancellationToken cancellationToken = default) => Failure is { } failure
            ? ValueTask.FromException<PeerRequestRecord?>(failure) : ValueTask.FromResult(record);
    }

    private sealed class Contracts : IPeerChannelContracts
    {
        public bool Allowed { get; set; } = true;
        public PeerChannelRejectionAction OnRejection { get; set; } = PeerChannelRejectionAction.None;
        public int Limit { get; set; } = 20;
        public Exception? Failure { get; set; }
        public PeerChannelMessageType[] Types { get; set; } = [PeerChannelMessageType.PeerRequest, PeerChannelMessageType.Memo];
        public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(OrganizationId organizationId,
            UnitId fromUnitId, UnitId toUnitId, CancellationToken cancellationToken = default) => Failure is { } failure
            ? ValueTask.FromException<PeerChannelConfiguration?>(failure)
            : ValueTask.FromResult<PeerChannelConfiguration?>(Allowed
                ? new PeerChannelConfiguration(fromUnitId, Types, Limit, OnRejection) : null);
    }

    private sealed class Registrar : IPeerRequestRegistrar
    {
        public ConcurrentDictionary<string, IActorRef> Actors { get; } = new();
        public ConcurrentQueue<PeerRequest> Requests { get; } = new();
        public bool Unavailable { get; set; }
        public ConcurrentQueue<RecordPeerRequestRejection> Rejections { get; } = new();
        public async Task<AcceptMessageResult> RecordRejectionAsync(ActorSystem system, RecordPeerRequestRejection rejection)
        {
            Rejections.Enqueue(rejection);
            if (Unavailable) throw new InvalidOperationException("requester unavailable");
            return await Actors[((PositionEndpointRef)rejection.Request.From).PositionId.Value]
                .Ask<AcceptMessageResult>(rejection, Timeout);
        }
        public TaskCompletionSource<bool>? Barrier { get; set; }
        public async Task<PeerRequestLookupResult> RecordAsync(ActorSystem system, PeerRequest request)
        {
            Requests.Enqueue(request);
            if (Barrier is not null) await Barrier.Task;
            if (Unavailable) throw new InvalidOperationException("requester unavailable");
            return await Actors[((PositionEndpointRef)request.From).PositionId.Value]
                .Ask<PeerRequestLookupResult>(new RecordPeerRequest(request), Timeout);
        }
    }

    private sealed class Projections : IPositionProjectionPublisher
    {
        public ConcurrentQueue<PositionProjectionEvent> Events { get; } = new();
        public void Publish(PositionProjectionEvent @event) => Events.Enqueue(@event);
    }

    private sealed class CaptureFactory : IPositionOccupantFactory
    {
        public TaskCompletionSource<OrgMessage> Delivered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Props Create(PositionOccupantActivation activation) => Props.Create(() => new CaptureActor(Delivered));
    }

    private sealed class CaptureActor : ReceiveActor
    {
        public CaptureActor(TaskCompletionSource<OrgMessage> delivered) =>
            Receive<OrgMessage>(message => delivered.TrySetResult(message));
    }

    private sealed class Provider : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(PositionEntityId entity,
            CancellationToken cancellationToken) => Task.FromResult(PositionRuntimeConfigurationLoadResult.Loaded(
            new PositionRuntimeConfiguration(new PositionConfigurationStamp(1, "sha256:horizontal"), entity.Organization,
                entity.Position, new PositionRuntimeDescriptor(Destination, null, entity.Position.Value, "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(OccupantType.Human), new PositionAuthorityRuntimeConfiguration([]))));
    }
}
