using Akka.Actor;
using Akka.Configuration;
using Akka.Pattern;
using Hive.Actors.Positions;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class PositionActorApprovalDecisionTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 8, 10, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Requester = PositionId.From("delivery-lead");
    private static readonly PositionId Approver = PositionId.From("ceo");
    private static readonly UnitId Unit = UnitId.From("delivery");

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "The proposed release carries unacceptable operational risk.")]
    public async Task Human_decision_is_validated_persisted_and_emitted_as_approval_decision(
        bool approved,
        string? reason)
    {
        var request = Request(to: Approver);
        var decisionId = MessageId.From(
            Guid.Parse("84000000-0000-0000-0000-000000000001"));

        var capture = await EmitAsync(
            Approver,
            request,
            new EmitOccupantApprovalDecision(
                request.Id,
                decisionId,
                request.Thread,
                Requester,
                request.Priority,
                OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
                approved,
                reason));

        var decision = Assert.IsType<ApprovalDecision>(capture.Result.Message);
        Assert.Equal(decisionId, decision.Id);
        Assert.Equal(request.Id, decision.RequestId);
        Assert.Equal(request.Thread, decision.Thread);
        Assert.Equal(new PositionEndpointRef(Approver), decision.From);
        Assert.Equal(request.From, decision.To);
        Assert.Equal(approved, decision.Approved);
        Assert.Equal(reason, decision.Reason);
        Assert.Equal(decision, capture.RoutedMessage);
        var persisted = Assert.Single(capture.State.OccupantReplies);
        Assert.Equal(request.Id, persisted.SourceMessageId);
        Assert.Equal(OccupantReplyAuthorKind.HumanUser, persisted.Author.Kind);
        Assert.Equal("person-alice", persisted.Author.SubjectId);
        Assert.Equal("web-inbox", persisted.Author.Channel);
        Assert.Equal(decision, persisted.Message);
    }

    [Fact]
    public async Task Position_cannot_decide_a_request_addressed_to_another_approver()
    {
        var request = Request(to: Requester);

        var capture = await EmitAsync(
            Approver,
            request,
            new EmitOccupantApprovalDecision(
                request.Id,
                MessageId.From(Guid.Parse("84000000-0000-0000-0000-000000000002")),
                request.Thread,
                Requester,
                request.Priority,
                OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
                approved: true));

        Assert.False(capture.Result.IsAccepted);
        var error = Assert.Single(capture.Result.Errors);
        Assert.Equal(ApprovalValidationCatalog.Codes.UnauthorizedApprover, error.Code);
        Assert.Equal(RejectionReason.Unauthorized, error.Reason);
        AssertGovernanceRejection(
            capture,
            ApprovalValidationCatalog.Codes.UnauthorizedApprover,
            RejectionReason.Unauthorized);
        Assert.Null(capture.RoutedMessage);
        Assert.Empty(capture.State.OccupantReplies);
    }

    [Fact]
    public async Task Decision_without_a_correlated_request_is_rejected_and_audited()
    {
        var request = Request(to: Approver);

        var capture = await EmitAsync(
            Approver,
            null,
            DecisionCommand(
                request,
                Guid.Parse("84000000-0000-0000-0000-000000000003")));

        AssertGovernanceRejection(
            capture,
            ApprovalValidationCatalog.Codes.ApprovalRequestNotFound,
            RejectionReason.InvalidRoute);
        Assert.Null(capture.RoutedMessage);
        Assert.Empty(capture.State.OccupantReplies);
    }

    [Fact]
    public async Task Decision_after_the_approval_window_is_rejected_and_audited()
    {
        var request = Request(to: Approver, deadline: At.AddSeconds(30));

        var capture = await EmitAsync(
            Approver,
            request,
            DecisionCommand(
                request,
                Guid.Parse("84000000-0000-0000-0000-000000000004")));

        AssertGovernanceRejection(
            capture,
            ApprovalValidationCatalog.Codes.ApprovalDecisionExpired,
            RejectionReason.Expired);
        Assert.Null(capture.RoutedMessage);
        Assert.Empty(capture.State.OccupantReplies);
    }

    [Fact]
    public async Task Authenticated_email_thread_mismatch_is_rejected_by_governance_and_audited()
    {
        var request = Request(to: Approver);
        var command = new EmitOccupantApprovalDecision(
            request.Id,
            MessageId.From(Guid.Parse("84000000-0000-0000-0000-000000000007")),
            ThreadId.From(Guid.Parse("82000000-0000-0000-0000-000000000099")),
            Requester,
            request.Priority,
            OccupantReplyAuthor.HumanUser("person-alice", "email"),
            approved: true);

        var capture = await EmitAsync(Approver, request, command);

        AssertGovernanceRejection(
            capture,
            ApprovalValidationCatalog.Codes.ApprovalThreadMismatch,
            RejectionReason.InvalidRoute,
            expectedChannel: "email");
        Assert.Null(capture.RoutedMessage);
        Assert.Empty(capture.State.OccupantReplies);
    }

    [Fact]
    public async Task Second_decision_for_the_same_request_is_rejected_and_audited()
    {
        var request = Request(to: Approver);
        var first = DecisionCommand(
            request,
            Guid.Parse("84000000-0000-0000-0000-000000000005"));
        var duplicate = DecisionCommand(
            request,
            Guid.Parse("84000000-0000-0000-0000-000000000006"));

        var capture = await EmitManyAsync(Approver, request, first, duplicate);

        Assert.True(capture.Results[0].IsAccepted);
        Assert.False(capture.Results[1].IsAccepted);
        var audit = Assert.Single(
            capture.ProjectionEvents.OfType<PositionApprovalDecisionRejected>());
        var error = Assert.Single(audit.Rejection.AuditResult.Errors);
        Assert.Equal(ApprovalValidationCatalog.Codes.ApprovalDecisionDuplicate, error.Code);
        Assert.Equal(RejectionReason.Duplicate, error.Reason);
        Assert.Equal(duplicate.DecisionMessageId, audit.Rejection.Context.MessageId);
        Assert.Single(capture.State.OccupantReplies);
    }

    private static ApprovalRequest Request(
        PositionId to,
        DateTimeOffset? deadline = null) => new(
        MessageId.From(Guid.Parse("81000000-0000-0000-0000-000000000001")),
        Organization,
        new PositionEndpointRef(Requester),
        new PositionEndpointRef(to),
        ThreadId.From(Guid.Parse("82000000-0000-0000-0000-000000000001")),
        Priority.Critical,
        1,
        At,
        deadline,
        "publish external release statement",
        "The release is ready for external publication.",
        ApprovalPolicyRef.From("comms.external-official"));

    private static EmitOccupantApprovalDecision DecisionCommand(
        ApprovalRequest request,
        Guid decisionMessageId) => new(
            request.Id,
            MessageId.From(decisionMessageId),
            request.Thread,
            Requester,
            request.Priority,
            OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
            approved: true);

    private static void AssertGovernanceRejection(
        EmissionCapture capture,
        string code,
        RejectionReason reason,
        string expectedChannel = "web-inbox")
    {
        Assert.False(capture.Result.IsAccepted);
        var resultError = Assert.Single(capture.Result.Errors);
        Assert.Equal(code, resultError.Code);
        Assert.Equal(reason, resultError.Reason);
        var audit = Assert.Single(
            capture.ProjectionEvents.OfType<PositionApprovalDecisionRejected>());
        var auditError = Assert.Single(audit.Rejection.AuditResult.Errors);
        Assert.Equal(code, auditError.Code);
        Assert.Equal(reason, auditError.Reason);
        Assert.Equal(capture.Result.SourceMessageId, audit.RequestId);
        Assert.Equal("person-alice", audit.Author.SubjectId);
        Assert.Equal(expectedChannel, audit.Author.Channel);
    }

    private static async Task<EmissionCapture> EmitAsync(
        PositionId sourcePosition,
        ApprovalRequest? source,
        EmitOccupantApprovalDecision command)
    {
        var capture = await EmitManyAsync(sourcePosition, source, command);
        return new EmissionCapture(
            Assert.Single(capture.Results),
            capture.RoutedMessage,
            capture.State,
            capture.ProjectionEvents);
    }

    private static async Task<EmissionSequenceCapture> EmitManyAsync(
        PositionId sourcePosition,
        ApprovalRequest? source,
        params EmitOccupantApprovalDecision[] commands)
    {
        var entity = PositionEntityId.From(Organization, sourcePosition);
        var emitter = new CapturingMessageEmitter();
        var projectionPublisher = new CapturingProjectionPublisher();
        var system = CreateActorSystem();
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new PositionActor(
                    entity.Value,
                    LoadedProvider(entity),
                    PositionOccupantFactory.Instance,
                    projectionPublisher,
                    () => At.AddMinutes(1),
                    null,
                    new OccupantReplyMessageValidator(
                        Relations(),
                        new FixedTimeProvider(At.AddMinutes(1))),
                    emitter)),
                $"position-approval-decision-{Guid.NewGuid():N}");
            await WaitForReadyAsync(actor);
            if (source is not null)
            {
                actor.Tell(new AcceptMessage(source));
                await WaitForSourceAsync(actor, source.Id);
            }

            var results = new List<OccupantReplyEmissionResult>();
            foreach (var command in commands)
            {
                results.Add(await actor.Ask<OccupantReplyEmissionResult>(command, Timeout()));
            }

            var state = await actor.Ask<PositionState>(GetPositionState.Instance, Timeout());
            return new EmissionSequenceCapture(
                results,
                emitter.Message,
                state,
                projectionPublisher.Events);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static MaterializedOrganizationRelations Relations()
    {
        var builder = OrganizationRelationsSnapshot.CreateBuilder(
            Organization,
            new OrganizationOwnerEndpointRef());
        builder.AddPosition(Approver, Unit);
        builder.AddPosition(Requester, Unit, Approver);
        return new MaterializedOrganizationRelations(builder.Build());
    }

    private static IPositionConfigurationProvider LoadedProvider(PositionEntityId entity) =>
        new StaticConfigurationProvider(
            PositionRuntimeConfigurationLoadResult.Loaded(new PositionRuntimeConfiguration(
                new PositionConfigurationStamp(1, "sha256:approval-decision-v1"),
                entity.Organization,
                entity.Position,
                new PositionRuntimeDescriptor(
                    Unit,
                    entity.Position == Approver ? null : Approver,
                    name: entity.Position.Value,
                    timezone: "Europe/Lisbon"),
                new OccupantRuntimeConfiguration(OccupantType.Human),
                new PositionAuthorityRuntimeConfiguration([]))));

    private static ActorSystem CreateActorSystem() =>
        ActorSystem.Create(
            $"position-approval-decision-tests-{Guid.NewGuid():N}",
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

        throw new TimeoutException("PositionActor did not become ready.");
    }

    private static async Task WaitForSourceAsync(IActorRef actor, MessageId sourceMessageId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await actor.Ask<PositionState>(
                GetPositionState.Instance,
                TimeSpan.FromSeconds(1));
            if (state.ProcessedMessages.Contains(sourceMessageId))
            {
                return;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Approval request was not persisted.");
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticConfigurationProvider(
        PositionRuntimeConfigurationLoadResult result) : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(result);
    }

    private sealed class CapturingMessageEmitter : IPositionMessageEmitter
    {
        public OrgMessage? Message { get; private set; }

        public void Emit(ActorSystem system, OrgMessage message) => Message = message;
    }

    private sealed class CapturingProjectionPublisher : IPositionProjectionPublisher
    {
        public List<PositionProjectionEvent> Events { get; } = [];

        public void Publish(PositionProjectionEvent @event) => Events.Add(@event);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed record EmissionCapture(
        OccupantReplyEmissionResult Result,
        OrgMessage? RoutedMessage,
        PositionState State,
        IReadOnlyList<PositionProjectionEvent> ProjectionEvents);

    private sealed record EmissionSequenceCapture(
        IReadOnlyList<OccupantReplyEmissionResult> Results,
        OrgMessage? RoutedMessage,
        PositionState State,
        IReadOnlyList<PositionProjectionEvent> ProjectionEvents);
}
