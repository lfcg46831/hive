using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Tests;

public sealed class PositionLiveStateFactMapperTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 3, 10, 0, 0, TimeSpan.Zero);

    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionEntityId Engineer = PositionEntityId.Parse("acme/engineer");
    private static readonly PositionEntityId Lead = PositionEntityId.Parse("acme/delivery-lead");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("10000000-0000-0000-0000-000000000001"));

    [Fact]
    public void Tasks_and_occupant_processing_drive_working_while_lifecycle_noise_is_ignored()
    {
        var mapper = new PositionLiveStateFactMapper();
        var taskId = PositionTaskId.From(Guid.Parse("20000000-0000-0000-0000-000000000001"));
        var messageId = MessageId.From(Guid.Parse("30000000-0000-0000-0000-000000000001"));

        var opened = mapper.Apply(PositionFact(
            Engineer,
            new TaskCreated(taskId, Thread, "Triage regression", Priority.High, At)));

        Assert.NotNull(opened);
        Assert.Equal(PositionLiveState.Idle, opened.PreviousState);
        Assert.Equal(PositionLiveState.Working, opened.State);
        Assert.Equal(nameof(TaskCreated), opened.CorrelatedEvent?.Type);
        Assert.Equal(Thread.Value, opened.CorrelatedEvent?.ThreadId);

        Assert.Null(mapper.Apply(PositionFact(
            Engineer,
            new TaskUpdated(taskId, "Reproduced", At.AddMinutes(1)))));
        Assert.Null(mapper.Apply(PositionFact(
            Engineer,
            new PositionPassivated(At.AddMinutes(2), "rebalance"))));
        Assert.Equal(PositionLiveState.Working, mapper.CurrentState(Engineer));

        var dispatched = mapper.Apply(PositionFact(
            Engineer,
            new MessageDispatched(
                messageId,
                Thread,
                OccupantId.From("agent-1"),
                OccupantType.AiAgent,
                At.AddMinutes(4))));
        Assert.Equal(PositionLiveState.Working, dispatched?.State);

        var taskCompleted = mapper.Apply(PositionFact(
            Engineer,
            new TaskCompleted(taskId, At.AddMinutes(5), "Fixed")));
        Assert.Equal(PositionLiveState.Working, taskCompleted?.State);
        Assert.Equal(Thread.Value, taskCompleted?.CorrelatedEvent?.ThreadId);

        var processingCompleted = mapper.Apply(PositionFact(
            Engineer,
            new MessageProcessingCompleted(
                "message:1",
                messageId,
                Thread,
                MessageProcessingCompletionStatus.Completed,
                At.AddMinutes(6))));
        Assert.Equal(PositionLiveState.Idle, processingCompleted?.State);
    }

    [Fact]
    public void Escalation_blocks_the_emitter_until_a_resolution_in_the_same_thread()
    {
        var mapper = new PositionLiveStateFactMapper();
        var escalation = EscalationMessage();

        var blocked = mapper.Apply(MessageFact(Lead, escalation));

        Assert.Equal(Engineer, blocked?.EntityId);
        Assert.Equal(PositionLiveState.Blocked, blocked?.State);
        Assert.Equal(nameof(Escalation), blocked?.CorrelatedEvent?.Type);

        Assert.Null(mapper.Apply(MessageFact(
            Engineer,
            new Report(
                MessageId.New(),
                Organization,
                new PositionEndpointRef(Lead.Position),
                new PositionEndpointRef(Engineer.Position),
                Thread,
                Priority.Normal,
                1,
                At.AddMinutes(1),
                null,
                DirectiveId.New(),
                ReportKind.Progress,
                "Still investigating"))));
        Assert.Equal(PositionLiveState.Blocked, mapper.CurrentState(Engineer));

        var resolved = mapper.Apply(MessageFact(
            Engineer,
            new Directive(
                MessageId.New(),
                Organization,
                new PositionEndpointRef(Lead.Position),
                new PositionEndpointRef(Engineer.Position),
                Thread,
                Priority.High,
                1,
                At.AddMinutes(2),
                null,
                DirectiveId.New(),
                null,
                "Proceed with rollback",
                "Resolution for the escalation")));

        Assert.Equal(PositionLiveState.Blocked, resolved?.PreviousState);
        Assert.Equal(PositionLiveState.Idle, resolved?.State);
        Assert.Equal(nameof(Directive), resolved?.EventType);
    }

    [Fact]
    public void Approval_request_waits_on_the_requester_until_the_correlated_decision()
    {
        var mapper = new PositionLiveStateFactMapper();
        var request = new ApprovalRequest(
            MessageId.From(Guid.Parse("40000000-0000-0000-0000-000000000001")),
            Organization,
            new PositionEndpointRef(Engineer.Position),
            new PositionEndpointRef(Lead.Position),
            Thread,
            Priority.High,
            1,
            At,
            null,
            "Deploy hotfix",
            "Production is degraded",
            ApprovalPolicyRef.From("production/change"));

        var waiting = mapper.Apply(MessageFact(Lead, request));

        Assert.Equal(Engineer, waiting?.EntityId);
        Assert.Equal(PositionLiveState.WaitingHuman, waiting?.State);

        Assert.Null(mapper.Apply(MessageFact(
            Engineer,
            ApprovalDecisionMessage(MessageId.New()))));
        Assert.Equal(PositionLiveState.WaitingHuman, mapper.CurrentState(Engineer));

        var decided = mapper.Apply(MessageFact(
            Engineer,
            ApprovalDecisionMessage(request.Id)));

        Assert.Equal(PositionLiveState.Idle, decided?.State);
        Assert.Equal(nameof(ApprovalDecision), decided?.CorrelatedEvent?.Type);
    }

    [Fact]
    public void Audit_and_message_facts_without_operational_semantics_are_ignored()
    {
        var mapper = new PositionLiveStateFactMapper();
        var auditFact = new PositionLiveStateProjectionFact(
            PositionLiveStateProjectionSource.AuditLog,
            sourceOffset: 1,
            Organization,
            nameof(JourneyAuditStage.GatewayCalled),
            At,
            "{}");

        Assert.Null(mapper.Apply(auditFact));
        Assert.Null(mapper.Apply(new PositionLiveStateProjectionFact(
            PositionLiveStateProjectionSource.AuditLog,
            sourceOffset: 2,
            Organization,
            nameof(JourneyAuditStage.ConnectorInbound),
            At,
            "{}")));
        Assert.Null(mapper.Apply(MessageFact(
            Engineer,
            new Memo(
                MessageId.New(),
                Organization,
                new PositionEndpointRef(Lead.Position),
                new PositionEndpointRef(Engineer.Position),
                Thread,
                Priority.Normal,
                1,
                At,
                null,
                "Informational only"))));
        Assert.Equal(PositionLiveState.Idle, mapper.CurrentState(Engineer));
    }

    [Fact]
    public void Canonical_precedence_is_offline_then_blocked_then_waiting_human()
    {
        var mapper = new PositionLiveStateFactMapper();
        var action = RetainedAction();
        var retained = mapper.Apply(PositionFact(Engineer, new ActionRetained(action)));
        Assert.Equal(PositionLiveState.WaitingHuman, retained?.State);

        var blocked = mapper.Apply(MessageFact(Lead, EscalationMessage()));
        Assert.Equal(PositionLiveState.Blocked, blocked?.State);

        var outsideHours = mapper.Apply(new PositionLiveStateConditionFact(
            Engineer,
            PositionLiveStateCondition.OutsideWorkingHours,
            isActive: true,
            At.AddMinutes(1)));
        Assert.Equal(PositionLiveState.Offline, outsideHours?.State);

        var backInsideHours = mapper.Apply(new PositionLiveStateConditionFact(
            Engineer,
            PositionLiveStateCondition.OutsideWorkingHours,
            isActive: false,
            At.AddMinutes(2)));
        Assert.Equal(PositionLiveState.Blocked, backInsideHours?.State);

        var grant = Grant(action);
        var escalationResolved = mapper.Apply(MessageFact(Engineer, grant));
        Assert.Equal(PositionLiveState.WaitingHuman, escalationResolved?.State);

        var authorized = mapper.Apply(PositionFact(
            Engineer,
            new RetainedActionAuthorized(grant, At.AddMinutes(4))));
        Assert.False(authorized?.StateChanged);
        Assert.Equal(PositionLiveState.WaitingHuman, authorized?.State);

        var configurationBlocked = mapper.Apply(new PositionLiveStateConditionFact(
            Engineer,
            PositionLiveStateCondition.ConfigurationBlocked,
            isActive: true,
            At.AddMinutes(5)));
        Assert.Equal(PositionLiveState.Blocked, configurationBlocked?.State);

        var configurationReady = mapper.Apply(new PositionLiveStateConditionFact(
            Engineer,
            PositionLiveStateCondition.ConfigurationBlocked,
            isActive: false,
            At.AddMinutes(6)));
        Assert.Equal(PositionLiveState.WaitingHuman, configurationReady?.State);

        var consumed = mapper.Apply(PositionFact(
            Engineer,
            new RetainedActionConsumed(action.Id, grant.Id, At.AddMinutes(7))));
        Assert.Equal(PositionLiveState.Idle, consumed?.State);

        var killSwitch = mapper.Apply(new PositionLiveStateConditionFact(
            Engineer,
            PositionLiveStateCondition.KillSwitch,
            isActive: true,
            At.AddMinutes(8)));
        Assert.Equal(PositionLiveState.Offline, killSwitch?.State);

        var killSwitchCleared = mapper.Apply(new PositionLiveStateConditionFact(
            Engineer,
            PositionLiveStateCondition.KillSwitch,
            isActive: false,
            At.AddMinutes(9)));
        Assert.Equal(PositionLiveState.Idle, killSwitchCleared?.State);
    }

    private static PositionLiveStateProjectionFact PositionFact(
        PositionEntityId entityId,
        PositionEvent @event) =>
        Assert.Single(
            PositionLiveStateProjectionWorker.Facts(JournalEvent(entityId, @event)),
            fact => fact.Source == PositionLiveStateProjectionSource.PositionEvent);

    private static PositionLiveStateProjectionFact MessageFact(
        PositionEntityId receivingEntity,
        OrgMessage message) =>
        Assert.Single(
            PositionLiveStateProjectionWorker.Facts(JournalEvent(
                receivingEntity,
                new MessageReceived(message, message.SentAt.AddSeconds(1)))),
            fact => fact.Source == PositionLiveStateProjectionSource.OrganizationalMessage);

    private static PositionLiveStateProjectionJournalEvent JournalEvent(
        PositionEntityId entityId,
        PositionEvent @event) =>
        new(
            offset: 1,
            $"position:{entityId.Value}",
            persistenceSequence: 1,
            entityId,
            @event);

    private static Escalation EscalationMessage() =>
        new(
            MessageId.From(Guid.Parse("50000000-0000-0000-0000-000000000001")),
            Organization,
            new PositionEndpointRef(Engineer.Position),
            new PositionEndpointRef(Lead.Position),
            Thread,
            Priority.High,
            1,
            At,
            null,
            "Deployment is blocked",
            "Credential is unavailable",
            ["Wait", "Roll back"]);

    private static ApprovalDecision ApprovalDecisionMessage(MessageId requestId) =>
        new(
            MessageId.New(),
            Organization,
            new PositionEndpointRef(Lead.Position),
            new PositionEndpointRef(Engineer.Position),
            Thread,
            Priority.High,
            1,
            At.AddMinutes(1),
            null,
            requestId,
            approved: true,
            "Approved");

    private static PersistedRetainedAction RetainedAction() =>
        new(
            RetainedActionId.From(Guid.Parse("60000000-0000-0000-0000-000000000001")),
            ActionFingerprint.From(
                "sha256:0000000000000000000000000000000000000000000000000000000000000001"),
            RetainedActionKind.Tool,
            "github.create-issue",
            "{\"title\":\"Incident\"}",
            "{\"repository\":\"acme/hive\"}",
            "directive:live-state",
            Organization,
            Engineer.Position,
            Thread,
            MessageId.New(),
            DirectiveId.New(),
            null,
            "action-gate-escalation-required",
            At);

    private static AuthorizationGrant Grant(PersistedRetainedAction action) =>
        new(
            MessageId.From(Guid.Parse("70000000-0000-0000-0000-000000000001")),
            Organization,
            new PositionEndpointRef(Lead.Position),
            new PositionEndpointRef(Engineer.Position),
            Thread,
            Priority.High,
            1,
            At.AddMinutes(3),
            null,
            MessageId.From(Guid.Parse("50000000-0000-0000-0000-000000000001")),
            action.Id,
            action.Fingerprint,
            AuthorityKey.From("governance.authorize-retained-action"),
            At.AddHours(1),
            "Approved");
}
