using Hive.Actors.Inbox;
using Hive.Domain.Directives;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Tests;

public sealed class InboxProjectionFactMapperTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 4, 10, 0, 0, TimeSpan.Zero);

    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionEntityId Engineer = PositionEntityId.Parse("acme/engineer");
    private static readonly PositionEntityId Lead = PositionEntityId.Parse("acme/delivery-lead");

    [Fact]
    public void Supported_messages_create_one_item_for_the_recipient_position()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var directive = DirectiveMessage(
            messageId: Message("10000000-0000-0000-0000-000000000001"),
            threadId: Thread("20000000-0000-0000-0000-000000000001"),
            directiveId: Directive("30000000-0000-0000-0000-000000000001"),
            deadline: At.AddHours(2));

        var change = Assert.Single(mapper.Apply(MessageFact(Engineer, directive)));

        Assert.Equal(Organization, change.Item.Key.OrganizationId);
        Assert.Equal(Engineer.Position, change.Item.Key.AssignedPositionId);
        Assert.Equal(directive.Id, change.Item.Key.MessageId);
        Assert.Equal(InboxProjectionMessageType.Directive, change.Item.Type);
        Assert.Equal(directive.From, change.Item.Origin);
        Assert.Equal(directive.To, change.Item.Destination);
        Assert.Equal(directive.Thread, change.Item.ThreadId);
        Assert.Equal(Priority.High, change.Item.Priority);
        Assert.Equal(directive.SentAt, change.Item.SentAtUtc);
        Assert.Equal(directive.Deadline, change.Item.DeadlineAtUtc);
        Assert.Equal(InboxProjectionResponseState.AwaitingResponse, change.Item.ResponseState);
        Assert.False(change.Item.IsExpired);
        Assert.Null(change.Item.LastReminderAtUtc);
        Assert.Null(change.Item.Approval);
        var directiveContent = Assert.IsType<InboxProjectionDirectiveContent>(change.Item.Content);
        Assert.Equal(directive.Objective, directiveContent.Objective);
        Assert.Equal(directive.Context, directiveContent.Context);

        var memo = new Memo(
            Message("10000000-0000-0000-0000-000000000002"),
            Organization,
            Position(Lead),
            Position(Engineer),
            Thread("20000000-0000-0000-0000-000000000002"),
            Priority.Normal,
            schemaVersion: 1,
            At.AddMinutes(1),
            deadline: null,
            "For information");
        var memoChange = Assert.Single(mapper.Apply(MessageFact(Engineer, memo)));

        Assert.Equal(InboxProjectionMessageType.Memo, memoChange.Item.Type);
        Assert.Equal(InboxProjectionResponseState.NotApplicable, memoChange.Item.ResponseState);
        Assert.Equal(
            memo.Body,
            Assert.IsType<InboxProjectionMemoContent>(memoChange.Item.Content).Body);
    }

    [Fact]
    public void Canonical_responses_only_close_the_exact_correlated_item()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var directiveId = Directive("30000000-0000-0000-0000-000000000010");
        var directive = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000010"),
            Thread("20000000-0000-0000-0000-000000000010"),
            directiveId);
        mapper.Apply(MessageFact(Engineer, directive));

        var unrelatedReport = ReportMessage(
            Message("10000000-0000-0000-0000-000000000011"),
            directive.Thread,
            Directive("30000000-0000-0000-0000-000000000011"));
        var reportItem = Assert.Single(
            mapper.Apply(MessageFact(Lead, unrelatedReport)),
            change => change.Item.Key.MessageId == unrelatedReport.Id).Item;
        var reportContent = Assert.IsType<InboxProjectionReportContent>(reportItem.Content);
        Assert.Equal(unrelatedReport.Body, reportContent.Body);
        Assert.Equal(unrelatedReport.Kind, reportContent.Kind);
        Assert.Equal(
            InboxProjectionResponseState.AwaitingResponse,
            Current(mapper, Engineer, directive.Id).ResponseState);

        var report = ReportMessage(
            Message("10000000-0000-0000-0000-000000000012"),
            directive.Thread,
            directiveId);
        var reportChanges = mapper.Apply(MessageFact(Lead, report));
        Assert.Contains(
            reportChanges,
            change => change.Item.Key.MessageId == directive.Id
                && change.Item.ResponseState == InboxProjectionResponseState.Responded);

        var peerRequest = new PeerRequest(
            Message("10000000-0000-0000-0000-000000000020"),
            Organization,
            Position(Engineer),
            Position(Lead),
            Thread("20000000-0000-0000-0000-000000000020"),
            Priority.Normal,
            schemaVersion: 1,
            At,
            deadline: null,
            "Can you review this?");
        var peerRequestItem = Assert.Single(
            mapper.Apply(MessageFact(Lead, peerRequest)),
            change => change.Item.Key.MessageId == peerRequest.Id).Item;
        Assert.Equal(
            peerRequest.Ask,
            Assert.IsType<InboxProjectionPeerRequestContent>(peerRequestItem.Content).Ask);
        var peerResponse = new PeerResponse(
            Message("10000000-0000-0000-0000-000000000021"),
            Organization,
            Position(Lead),
            Position(Engineer),
            peerRequest.Thread,
            Priority.Normal,
            schemaVersion: 1,
            At.AddMinutes(1),
            deadline: null,
            peerRequest.Id,
            "Reviewed");
        var peerResponseItem = Assert.Single(
            mapper.Apply(MessageFact(Engineer, peerResponse)),
            change => change.Item.Key.MessageId == peerResponse.Id).Item;
        Assert.Equal(
            peerResponse.Body,
            Assert.IsType<InboxProjectionPeerResponseContent>(peerResponseItem.Content).Body);
        Assert.Equal(
            InboxProjectionResponseState.Responded,
            Current(mapper, Lead, peerRequest.Id).ResponseState);

        var escalation = new Escalation(
            Message("10000000-0000-0000-0000-000000000030"),
            Organization,
            Position(Engineer),
            Position(Lead),
            Thread("20000000-0000-0000-0000-000000000030"),
            Priority.Critical,
            schemaVersion: 1,
            At,
            deadline: null,
            "Deployment blocked",
            "Missing credential",
            ["Wait", "Roll back"]);
        var escalationItem = Assert.Single(
            mapper.Apply(MessageFact(Lead, escalation)),
            change => change.Item.Key.MessageId == escalation.Id).Item;
        var escalationContent = Assert.IsType<InboxProjectionEscalationContent>(
            escalationItem.Content);
        Assert.Equal(escalation.Issue, escalationContent.Issue);
        Assert.Equal(escalation.Context, escalationContent.Context);
        var resolution = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000031"),
            escalation.Thread,
            Directive("30000000-0000-0000-0000-000000000031"),
            from: Lead,
            to: Engineer);
        mapper.Apply(MessageFact(Engineer, resolution));
        Assert.Equal(
            InboxProjectionResponseState.Responded,
            Current(mapper, Lead, escalation.Id).ResponseState);
    }

    [Fact]
    public void Accepted_approval_decision_updates_the_request_and_inherits_its_metadata()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var request = ApprovalRequestMessage(
            Message("10000000-0000-0000-0000-000000000040"),
            Thread("20000000-0000-0000-0000-000000000040"),
            deadline: At.AddHours(1));
        var pending = Assert.Single(mapper.Apply(MessageFact(Lead, request))).Item;

        Assert.Equal(InboxProjectionApprovalState.Pending, pending.Approval?.State);
        Assert.Equal(request.Action, pending.Approval?.Action);
        Assert.Equal(request.Policy, pending.Approval?.Policy);
        var requestContent = Assert.IsType<InboxProjectionApprovalRequestContent>(
            pending.Content);
        Assert.Equal(request.Action, requestContent.Action);
        Assert.Equal(request.Justification, requestContent.Justification);

        var decision = ApprovalDecisionMessage(
            Message("10000000-0000-0000-0000-000000000041"),
            request,
            approved: false,
            sentAt: At.AddMinutes(5));
        var changes = mapper.Apply(MessageFact(Engineer, decision));

        var decidedRequest = Current(mapper, Lead, request.Id);
        Assert.Equal(InboxProjectionApprovalState.Rejected, decidedRequest.Approval?.State);
        Assert.Equal(decision.Id, decidedRequest.Approval?.DecisionMessageId);
        Assert.Equal(decision.SentAt, decidedRequest.Approval?.DecidedAtUtc);

        var decisionItem = Current(mapper, Engineer, decision.Id);
        Assert.Equal(InboxProjectionMessageType.ApprovalDecision, decisionItem.Type);
        Assert.Equal(request.Id, decisionItem.Approval?.RequestId);
        Assert.Equal(request.Action, decisionItem.Approval?.Action);
        Assert.Equal(request.Policy, decisionItem.Approval?.Policy);
        Assert.Equal(InboxProjectionApprovalState.Rejected, decisionItem.Approval?.State);
        Assert.Equal(
            decision.Reason,
            Assert.IsType<InboxProjectionApprovalDecisionContent>(decisionItem.Content).Reason);
        Assert.Contains(changes, change => change.Item.Key.MessageId == request.Id);
        Assert.Contains(changes, change => change.Item.Key.MessageId == decision.Id);
    }

    [Fact]
    public void Deadline_refresh_expires_pending_items_using_the_injected_clock()
    {
        var clock = new MutableTimeProvider(At);
        var mapper = new InboxProjectionFactMapper(clock);
        var deadline = At.AddMinutes(10);
        var directive = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000050"),
            Thread("20000000-0000-0000-0000-000000000050"),
            Directive("30000000-0000-0000-0000-000000000050"),
            deadline);
        var request = ApprovalRequestMessage(
            Message("10000000-0000-0000-0000-000000000051"),
            Thread("20000000-0000-0000-0000-000000000051"),
            deadline);
        mapper.Apply(MessageFact(Engineer, directive));
        mapper.Apply(MessageFact(Lead, request));

        clock.UtcNow = deadline;
        var changes = mapper.RefreshExpirations();

        Assert.Equal(2, changes.Count);
        Assert.All(changes, change =>
        {
            Assert.True(change.Item.IsExpired);
            Assert.Equal(InboxProjectionFactMapper.DeadlineExpiredFactType, change.FactType);
            Assert.Equal(deadline, change.OccurredAtUtc);
        });
        Assert.Equal(
            InboxProjectionApprovalState.Expired,
            Current(mapper, Lead, request.Id).Approval?.State);

        // An accepted decision is authoritative even when replay happens after its deadline.
        var acceptedBeforeDeadline = ApprovalDecisionMessage(
            Message("10000000-0000-0000-0000-000000000052"),
            request,
            approved: true,
            sentAt: deadline.AddSeconds(-1));
        mapper.Apply(MessageFact(Engineer, acceptedBeforeDeadline));
        Assert.Equal(
            InboxProjectionApprovalState.Approved,
            Current(mapper, Lead, request.Id).Approval?.State);
    }

    [Fact]
    public void Deadline_event_updates_the_source_item_without_rewriting_its_envelope()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var deadline = At.AddHours(2);
        var directive = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000053"),
            Thread("20000000-0000-0000-0000-000000000053"),
            Directive("30000000-0000-0000-0000-000000000053"),
            deadline);
        mapper.Apply(MessageFact(Engineer, directive));
        var remindedAt = deadline.AddMinutes(-30);
        var reminder = DeadlineReminder(
            directive,
            Message("10000000-0000-0000-0000-000000000054"),
            Thread("20000000-0000-0000-0000-000000000054"),
            remindedAt);

        var change = Assert.Single(mapper.Apply(MessageFact(Engineer, reminder)));

        Assert.Equal(directive.Id, change.Item.Key.MessageId);
        Assert.Equal(remindedAt, change.Item.LastReminderAtUtc);
        Assert.Equal(deadline, change.Item.DeadlineAtUtc);
        Assert.Equal(Priority.High, change.Item.Priority);
        Assert.Equal(
            InboxProjectionFactMapper.DeadlineApproachingEventType,
            change.FactType);
        Assert.Null(mapper.CurrentItem(Organization, Engineer.Position, reminder.Id));
        Assert.Empty(mapper.Apply(MessageFact(Engineer, reminder)));
    }

    [Fact]
    public void Deadline_event_rejects_invalid_or_mismatched_source_correlation()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var directive = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000056"),
            Thread("20000000-0000-0000-0000-000000000056"),
            Directive("30000000-0000-0000-0000-000000000056"),
            At.AddHours(1));
        mapper.Apply(MessageFact(Engineer, directive));
        var reminder = DeadlineReminder(
            directive,
            Message("10000000-0000-0000-0000-000000000057"),
            Thread("20000000-0000-0000-0000-000000000057"),
            At.AddMinutes(30),
            sourceMessageId: Message("10000000-0000-0000-0000-000000000058"));

        Assert.Throws<InvalidOperationException>(() =>
            mapper.Apply(MessageFact(Engineer, reminder)));
    }

    [Fact]
    public void Successful_report_audit_closes_a_directive_even_when_captured_first()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var directive = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000055"),
            Thread("20000000-0000-0000-0000-000000000055"),
            Directive("30000000-0000-0000-0000-000000000055"));
        var resultCreated = AuditFact(
            nameof(Hive.Domain.Auditing.JourneyAuditStage.ResultMessageCreated),
            "{\"outcome\":\"Succeeded\",\"message_type\":\"Report\"}",
            Engineer,
            directive.Id,
            directive.Thread);

        Assert.Empty(mapper.Apply(resultCreated));
        var item = Assert.Single(mapper.Apply(MessageFact(Engineer, directive))).Item;

        Assert.Equal(InboxProjectionResponseState.Responded, item.ResponseState);
    }

    [Fact]
    public void Actor_lifecycle_system_messages_and_known_audit_events_are_explicitly_ignored()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        Assert.Empty(mapper.Apply(PositionFact(
            Engineer,
            new PositionPassivated(At, "rebalance"))));

        var pulse = new Pulse(
            Message("10000000-0000-0000-0000-000000000060"),
            Organization,
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            Position(Engineer),
            Thread("20000000-0000-0000-0000-000000000060"),
            Priority.Normal,
            schemaVersion: 1,
            At,
            deadline: null,
            "daily",
            "{}");
        Assert.Empty(mapper.Apply(MessageFact(Engineer, pulse)));

        var unrelatedTrigger = new EventTrigger(
            Message("10000000-0000-0000-0000-000000000061"),
            Organization,
            new SystemEndpointRef(SystemEndpointKind.DomainEvents),
            Position(Engineer),
            Thread("20000000-0000-0000-0000-000000000061"),
            Priority.Normal,
            schemaVersion: 1,
            At,
            deadline: null,
            "position-blocked-prolonged",
            "not-json-because-this-event-is-not-an-inbox-reminder");
        Assert.Empty(mapper.Apply(MessageFact(Engineer, unrelatedTrigger)));
        Assert.Empty(mapper.Apply(AuditFact(nameof(Hive.Domain.Auditing.JourneyAuditStage.PositionAccepted))));
        Assert.Empty(mapper.Apply(AuditFact(nameof(Hive.Domain.Auditing.JourneyAuditStage.ConnectorInbound))));

        Assert.Throws<InvalidOperationException>(() =>
            mapper.Apply(AuditFact("FutureInboxRelevantStage")));
    }

    [Fact]
    public void Message_fact_must_match_the_persisted_recipient_and_identity()
    {
        var mapper = new InboxProjectionFactMapper(new MutableTimeProvider(At));
        var directive = DirectiveMessage(
            Message("10000000-0000-0000-0000-000000000070"),
            Thread("20000000-0000-0000-0000-000000000070"),
            Directive("30000000-0000-0000-0000-000000000070"));

        Assert.Throws<InvalidOperationException>(() =>
            mapper.Apply(MessageFact(Lead, directive)));
    }

    private static InboxProjectionItem Current(
        InboxProjectionFactMapper mapper,
        PositionEntityId entityId,
        MessageId messageId) =>
        mapper.CurrentItem(entityId.Organization, entityId.Position, messageId)
        ?? throw new Xunit.Sdk.XunitException($"Inbox item '{entityId}/{messageId}' was not mapped.");

    private static InboxProjectionFact PositionFact(
        PositionEntityId entityId,
        PositionEvent @event) =>
        Assert.Single(
            InboxProjectionWorker.Facts(JournalEvent(entityId, @event)),
            fact => fact.Source == InboxProjectionSource.PositionEvent);

    private static InboxProjectionFact MessageFact(
        PositionEntityId receivingEntity,
        OrgMessage message) =>
        Assert.Single(
            InboxProjectionWorker.Facts(JournalEvent(
                receivingEntity,
                new MessageReceived(message, message.SentAt.AddSeconds(1)))),
            fact => fact.Source == InboxProjectionSource.OrganizationalMessage);

    private static InboxProjectionFact AuditFact(
        string factType,
        string payloadJson = "{}",
        PositionEntityId? entityId = null,
        MessageId? messageId = null,
        ThreadId? threadId = null) =>
        new(
            InboxProjectionSource.AuditLog,
            sourceOffset: 1,
            Organization,
            factType,
            At,
            payloadJson,
            entityId?.Position,
            messageId: messageId,
            threadId: threadId);

    private static InboxProjectionJournalEvent JournalEvent(
        PositionEntityId entityId,
        PositionEvent @event) =>
        new(
            offset: 1,
            $"position:{entityId.Value}",
            persistenceSequence: 1,
            entityId,
            @event);

    private static Directive DirectiveMessage(
        MessageId messageId,
        ThreadId threadId,
        DirectiveId directiveId,
        DateTimeOffset? deadline = null,
        PositionEntityId? from = null,
        PositionEntityId? to = null) =>
        new(
            messageId,
            Organization,
            Position(from ?? Lead),
            Position(to ?? Engineer),
            threadId,
            Priority.High,
            schemaVersion: 1,
            At,
            deadline,
            directiveId,
            parentDirectiveId: null,
            "Investigate regression",
            "Production alert");

    private static Report ReportMessage(
        MessageId messageId,
        ThreadId threadId,
        DirectiveId aboutDirectiveId) =>
        new(
            messageId,
            Organization,
            Position(Engineer),
            Position(Lead),
            threadId,
            Priority.High,
            schemaVersion: 1,
            At.AddMinutes(1),
            deadline: null,
            aboutDirectiveId,
            ReportKind.Progress,
            "Investigation started");

    private static ApprovalRequest ApprovalRequestMessage(
        MessageId messageId,
        ThreadId threadId,
        DateTimeOffset? deadline) =>
        new(
            messageId,
            Organization,
            Position(Engineer),
            Position(Lead),
            threadId,
            Priority.High,
            schemaVersion: 1,
            At,
            deadline,
            "Deploy hotfix",
            "Production is degraded",
            ApprovalPolicyRef.From("production/change"));

    private static ApprovalDecision ApprovalDecisionMessage(
        MessageId messageId,
        ApprovalRequest request,
        bool approved,
        DateTimeOffset sentAt) =>
        new(
            messageId,
            Organization,
            request.To,
            request.From,
            request.Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt,
            deadline: null,
            request.Id,
            approved,
            approved ? "Approved" : "Rejected");

    private static EventTrigger DeadlineReminder(
        Directive source,
        MessageId messageId,
        ThreadId triggerThreadId,
        DateTimeOffset sentAt,
        MessageId? sourceMessageId = null) =>
        new(
            messageId,
            Organization,
            new SystemEndpointRef(SystemEndpointKind.DomainEvents),
            source.To,
            triggerThreadId,
            Priority.Low,
            schemaVersion: 1,
            sentAt,
            deadline: null,
            InboxProjectionFactMapper.DeadlineApproachingEventType,
            $$"""
            {
              "schema_version": 1,
              "source_message_id": "{{sourceMessageId ?? source.Id}}",
              "source_thread_id": "{{source.Thread}}",
              "deadline_at_utc": "{{source.Deadline:O}}"
            }
            """);

    private static PositionEndpointRef Position(PositionEntityId entityId) =>
        new(entityId.Position);

    private static MessageId Message(string value) => MessageId.From(Guid.Parse(value));

    private static ThreadId Thread(string value) => ThreadId.From(Guid.Parse(value));

    private static DirectiveId Directive(string value) => DirectiveId.From(Guid.Parse(value));

    private sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
