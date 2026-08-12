using Hive.Domain.Auditing;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;

namespace Hive.Tests;

public sealed class JourneyAuditPositionProjectionPublisherTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 8, 11, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionEntityId Entity = PositionEntityId.From(Organization, Position);
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001911"));
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001911"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001911"));

    [Fact]
    public void Publish_records_position_acceptance_and_dispatch_after_journal_commit_without_raw_message_text()
    {
        var audit = new RecordingJourneyAuditLog();
        var inner = new RecordingPositionProjectionPublisher();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit, inner);
        var directive = DirectiveMessage();

        publisher.Publish(new PositionEventCommitted(Entity, new MessageReceived(directive, At)));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageDispatched(
                Message,
                Thread,
                OccupantId.From("agent-14a"),
                OccupantType.AiAgent,
                At.AddSeconds(1))));

        Assert.Equal(2, inner.Events.Count);
        Assert.Equal(
            [JourneyAuditStage.PositionAccepted, JourneyAuditStage.PositionDispatched],
            audit.Records.Select(record => record.Stage));
        Assert.All(audit.Records, record =>
        {
            Assert.Equal(JourneyAuditOutcome.Accepted, record.Outcome);
            Assert.Equal(Organization, record.OrganizationId);
            Assert.Equal(Position, record.PositionId);
            Assert.Equal(Thread, record.ThreadId);
            Assert.Equal(Directive, record.DirectiveId);
            Assert.Equal(Message, record.MessageId);
            Assert.Equal("Directive", record.MessageType);
            Assert.DoesNotContain("Customer reports checkout failures", string.Join(" ", record.Payload.Values));
        });
    }

    [Fact]
    public void Publish_records_occupant_reply_authorship_and_correlation_without_raw_reply_text()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        var replyMessageId = MessageId.From(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000001912"));
        var report = new Report(
            replyMessageId,
            Organization,
            new PositionEndpointRef(Position),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            Thread,
            Priority.High,
            1,
            At.AddMinutes(5),
            deadline: null,
            Directive,
            ReportKind.Done,
            "Sensitive human-authored completion text.");

        publisher.Publish(new PositionEventCommitted(
            Entity,
            new OccupantReplyEmitted(
                Message,
                OccupantReplyAuthor.ExternalOccupant("remote-agent-7", "https-api"),
                report,
                At.AddMinutes(5))));

        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditStage.ResultMessageCreated, record.Stage);
        Assert.Equal(JourneyAuditOutcome.Succeeded, record.Outcome);
        Assert.Equal("occupant-reply-emitted", record.ReasonCode);
        Assert.Equal(Message, record.MessageId);
        Assert.Equal(Directive, record.DirectiveId);
        Assert.Equal(Position, record.PositionId);
        Assert.Equal(nameof(Report), record.MessageType);
        Assert.Equal("https-api", record.Payload["source"]);
        Assert.Equal("external-occupant", record.Payload["authorKind"]);
        Assert.Equal("remote-agent-7", record.Payload["authorSubjectId"]);
        Assert.Equal(replyMessageId.ToString(), record.Payload["resultMessageId"]);
        Assert.Equal("message.payload", record.Payload["redactions"]);
        Assert.DoesNotContain(
            "Sensitive human-authored completion text",
            string.Join(" ", record.Payload.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_defers_ai_result_audit_until_the_confirmed_handoff_adapter()
    {
        var audit = new RecordingJourneyAuditLog();
        var inner = new RecordingPositionProjectionPublisher();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit, inner);
        var report = new Report(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001916")),
            Organization,
            new PositionEndpointRef(Position),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            Thread,
            Priority.High,
            1,
            At.AddMinutes(5),
            deadline: null,
            Directive,
            ReportKind.Done,
            "Sensitive AI-authored completion text.");

        publisher.Publish(new PositionEventCommitted(
            Entity,
            new OccupantReplyEmitted(
                Message,
                OccupantReplyAuthor.AiAgent(OccupantId.From("agent-14a")),
                report,
                At.AddMinutes(5))));

        Assert.Empty(audit.Records);
        Assert.Single(inner.Events);
    }

    [Fact]
    public void Publish_audits_human_approval_decision_without_exposing_its_reason()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        var decisionMessageId = MessageId.From(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000001913"));
        var decision = new ApprovalDecision(
            decisionMessageId,
            Organization,
            new PositionEndpointRef(Position),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            Thread,
            Priority.Critical,
            1,
            At.AddMinutes(6),
            deadline: null,
            Message,
            approved: false,
            "Sensitive human rejection reason.");

        publisher.Publish(new PositionEventCommitted(
            Entity,
            new OccupantReplyEmitted(
                Message,
                OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
                decision,
                At.AddMinutes(6))));

        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditStage.ResultMessageCreated, record.Stage);
        Assert.Equal(JourneyAuditOutcome.Succeeded, record.Outcome);
        Assert.Equal("occupant-reply-emitted", record.ReasonCode);
        Assert.Equal(Message, record.MessageId);
        Assert.Equal(Position, record.PositionId);
        Assert.Equal(nameof(ApprovalDecision), record.MessageType);
        Assert.Equal("web-inbox", record.Payload["source"]);
        Assert.Equal("human-user", record.Payload["authorKind"]);
        Assert.Equal("person-alice", record.Payload["authorSubjectId"]);
        Assert.Equal(decisionMessageId.ToString(), record.Payload["resultMessageId"]);
        Assert.Equal(MessageChannel.Governance.ToString(), record.Payload["channel"]);
        Assert.DoesNotContain(
            "Sensitive human rejection reason",
            string.Join(" ", record.Payload.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_records_rejected_human_approval_decision_with_governance_errors_and_authorship()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        var decisionMessageId = MessageId.From(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000001914"));
        var requester = new PositionEndpointRef(PositionId.From("delivery-lead"));
        var decision = new ApprovalDecision(
            decisionMessageId,
            Organization,
            new PositionEndpointRef(Position),
            requester,
            Thread,
            Priority.Critical,
            1,
            At.AddMinutes(7),
            deadline: null,
            Message,
            approved: false,
            "Sensitive reason must not enter rejection audit payloads.");
        var rejection = RoutingRejection.Create(
            RoutingValidationContext.ForMessage(decision).WithGovernance(
                ApprovalPolicyRef.From("comms.external-official"),
                appliedVersion: null,
                new PositionEndpointRef(PositionId.From("ceo"))),
            ValidationResult.Create([
                ApprovalValidationCatalog.UnauthorizedApprover(),
                ApprovalValidationCatalog.ApprovalDecisionExpired(),
            ]));

        publisher.Publish(new PositionApprovalDecisionRejected(
            Entity,
            Message,
            OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
            rejection,
            At.AddMinutes(7)));

        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditStage.PositionAccepted, record.Stage);
        Assert.Equal(JourneyAuditOutcome.Rejected, record.Outcome);
        Assert.Equal(decisionMessageId, record.MessageId);
        Assert.Equal(Message, MessageId.From(Guid.Parse(record.Payload["requestMessageId"])));
        Assert.Equal(nameof(ApprovalDecision), record.MessageType);
        Assert.Equal("web-inbox", record.Payload["source"]);
        Assert.Equal("human-user", record.Payload["authorKind"]);
        Assert.Equal("person-alice", record.Payload["authorSubjectId"]);
        Assert.Equal("position:triage-agent", record.Payload["sender"]);
        Assert.Equal("position:delivery-lead", record.Payload["recipient"]);
        Assert.Equal("position:ceo", record.Payload["expectedApprover"]);
        Assert.Equal("comms.external-official", record.Payload["receivedPolicy"]);
        Assert.Equal("unknown", record.Payload["expectedPolicyVersion"]);
        Assert.Contains(
            ApprovalValidationCatalog.Codes.UnauthorizedApprover,
            record.Payload["errors"],
            StringComparison.Ordinal);
        Assert.Contains(
            ApprovalValidationCatalog.Codes.ApprovalDecisionExpired,
            record.Payload["errors"],
            StringComparison.Ordinal);
        Assert.Equal("reason", record.Payload["redactions"]);
        Assert.DoesNotContain(
            "Sensitive reason",
            string.Join(" ", record.Payload.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_records_deterministic_audit_ids_for_repeated_position_events()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        var directive = DirectiveMessage();

        publisher.Publish(new PositionEventCommitted(Entity, new MessageReceived(directive, At)));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageDispatched(
                Message,
                Thread,
                OccupantId.From("agent-14a"),
                OccupantType.AiAgent,
                At.AddSeconds(1))));
        publisher.Publish(new PositionEventCommitted(Entity, new MessageReceived(directive, At.AddSeconds(2))));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageDispatched(
                Message,
                Thread,
                OccupantId.From("agent-14a"),
                OccupantType.AiAgent,
                At.AddSeconds(3))));

        Assert.Equal(4, audit.Records.Count);
        Assert.Equal(audit.Records[0].AuditEventId, audit.Records[2].AuditEventId);
        Assert.Equal(audit.Records[1].AuditEventId, audit.Records[3].AuditEventId);
        Assert.NotEqual(audit.Records[0].AuditEventId, audit.Records[1].AuditEventId);
    }

    [Fact]
    public void Publish_records_duplicate_suppression_when_duplicate_message_has_terminal_result()
    {
        var audit = new RecordingJourneyAuditLog();
        var inner = new RecordingPositionProjectionPublisher();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit, inner);
        audit.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.ResultMessageCreated,
            JourneyAuditOutcome.Succeeded,
            Organization,
            Thread,
            Message,
            directiveId: Directive,
            positionId: Position,
            messageType: nameof(Report),
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["resultMessageType"] = nameof(Report),
                ["redactions"] = "resultMessage.report.body:free-text",
            }));

        publisher.Publish(new PositionMessageDuplicateRejected(
            Entity,
            Message,
            Thread,
            At.AddSeconds(5)));

        var suppression = Assert.Single(audit.Records.Where(record =>
            record.Stage == JourneyAuditStage.DuplicateSuppressed));
        Assert.Equal(JourneyAuditOutcome.Rejected, suppression.Outcome);
        Assert.Equal("terminal-result-already-materialized", suppression.ReasonCode);
        Assert.Equal(Organization, suppression.OrganizationId);
        Assert.Equal(Thread, suppression.ThreadId);
        Assert.Equal(Directive, suppression.DirectiveId);
        Assert.Equal(Message, suppression.MessageId);
        Assert.Equal(Position, suppression.PositionId);
        Assert.Equal(nameof(Report), suppression.MessageType);
        Assert.Equal("ResultMessageCreated", suppression.Payload["suppressedStage"]);
        Assert.Equal("Succeeded", suppression.Payload["suppressedOutcome"]);
        Assert.Equal(
            "directive.objective,directive.context,gateway.request.content,gateway.response.text",
            suppression.Payload["redactions"]);
        Assert.DoesNotContain("Customer reports checkout failures", string.Join(" ", suppression.Payload.Values));
        Assert.Contains(inner.Events, @event => @event is PositionMessageDuplicateRejected);
    }

    [Fact]
    public void Publish_projects_checkpoint_transitions_as_idempotent_minimized_timeline_entries()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageReceived(DirectiveMessage(), At)));
        var first = Checkpoint(
            revision: 1,
            completedIds: ["inspect"],
            blockers: [],
            nextSubtaskId: "verify");
        var second = Checkpoint(
            revision: 2,
            completedIds: ["inspect", "verify"],
            blockers: [OutcomeBlocker.ToolFailure],
            nextSubtaskId: null);

        publisher.Publish(new PositionEventCommitted(
            Entity,
            new DirectiveCheckpointPersisted(first, At.AddMinutes(1))));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new DirectiveCheckpointPersisted(second, At.AddMinutes(2))));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new DirectiveCheckpointPersisted(second, At.AddMinutes(3))));

        var records = audit.Records
            .Where(record =>
                record.Stage == JourneyAuditStage.DirectiveCheckpointTransition)
            .ToArray();
        Assert.Equal(3, records.Length);
        Assert.Equal(records[1].AuditEventId, records[2].AuditEventId);
        Assert.NotEqual(records[0].AuditEventId, records[1].AuditEventId);
        Assert.Equal(["checkpoint-created", "checkpoint-advanced", "checkpoint-advanced"],
            records.Select(record => record.ReasonCode));

        var advanced = records[1];
        Assert.Equal(Message, advanced.MessageId);
        Assert.Equal(Directive, advanced.DirectiveId);
        Assert.Equal(Thread, advanced.ThreadId);
        Assert.Equal(Position, advanced.PositionId);
        Assert.Equal(nameof(DirectiveCheckpointPersisted), advanced.MessageType);
        Assert.Equal(
            new[]
            {
                "blockerCodes",
                "blockerCount",
                "completedSubtaskCount",
                "completedSubtaskIds",
                "contractVersion",
                "nextSubtaskId",
                "parentDirectiveId",
                "planContractVersion",
                "plannedSubtaskCount",
                "positionTaskId",
                "redactions",
                "revision",
                "transition",
            },
            advanced.Payload.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("advanced", advanced.Payload["transition"]);
        Assert.Equal("1", advanced.Payload["contractVersion"]);
        Assert.Equal("1", advanced.Payload["planContractVersion"]);
        Assert.Equal("2", advanced.Payload["revision"]);
        Assert.Equal("2", advanced.Payload["plannedSubtaskCount"]);
        Assert.Equal("2", advanced.Payload["completedSubtaskCount"]);
        Assert.Equal("inspect,verify", advanced.Payload["completedSubtaskIds"]);
        Assert.Equal("1", advanced.Payload["blockerCount"]);
        Assert.Equal("ToolFailure", advanced.Payload["blockerCodes"]);
        Assert.Equal("none", advanced.Payload["nextSubtaskId"]);

        var timeline = new JourneyAuditReadModel(audit).ReadTimeline(
            Organization,
            Thread,
            Directive);
        Assert.Equal(
            3,
            timeline.Entries.Count(entry =>
                entry.Stage == JourneyAuditStage.DirectiveCheckpointTransition));
        var serializedTimeline = System.Text.Json.JsonSerializer.Serialize(timeline);
        Assert.DoesNotContain("Private inspection objective", serializedTimeline, StringComparison.Ordinal);
        Assert.DoesNotContain("secret criterion", serializedTimeline, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.evidence", serializedTimeline, StringComparison.Ordinal);
    }

    [Fact]
    public void Publish_audits_terminal_occupant_timeout_without_message_content_or_personal_endpoint()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageReceived(DirectiveMessage(), At)));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new OccupantResponseTimeoutHandled(
                Message,
                Thread,
                OccupantId.From("person-alice"),
                UserId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001911")),
                OccupantChannelBindingId.From(
                    Guid.Parse("eeeeeeee-0000-0000-0000-000000001911")),
                At.AddHours(16),
                At.AddHours(16),
                operationalAlert: true,
                killSwitchRequested: true)));

        var record = Assert.Single(
            audit.Records,
            candidate => candidate.Stage == JourneyAuditStage.OccupantResponseTimeout);

        Assert.Equal(JourneyAuditOutcome.Failed, record.Outcome);
        Assert.Equal("occupant-response-timeout-no-valid-target", record.ReasonCode);
        Assert.Equal(Message, record.MessageId);
        Assert.Equal(Directive, record.DirectiveId);
        Assert.Equal("true", record.Payload["operationalAlert"], ignoreCase: true);
        Assert.Equal("true", record.Payload["killSwitchRequested"], ignoreCase: true);
        Assert.Equal("none", record.Payload["target"]);
        Assert.DoesNotContain("Customer reports checkout failures", string.Join(" ", record.Payload.Values));
        Assert.DoesNotContain("owner@", string.Join(" ", record.Payload.Values));
    }

    [Fact]
    public void Publish_audits_terminal_occupant_absence_without_message_content_or_personal_endpoint()
    {
        var audit = new RecordingJourneyAuditLog();
        var publisher = new JourneyAuditPositionProjectionPublisher(audit);
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new MessageReceived(DirectiveMessage(), At)));
        publisher.Publish(new PositionEventCommitted(
            Entity,
            new OccupantAbsenceEscalationHandled(
                Message,
                Thread,
                OccupantId.From("person-alice"),
                At.AddMinutes(1),
                operationalAlert: true,
                killSwitchRequested: true)));

        var record = Assert.Single(
            audit.Records,
            candidate => candidate.Stage == JourneyAuditStage.OccupantAbsence);

        Assert.Equal(JourneyAuditOutcome.Failed, record.Outcome);
        Assert.Equal("occupant-absence-no-valid-target", record.ReasonCode);
        Assert.Equal(Message, record.MessageId);
        Assert.Equal(Directive, record.DirectiveId);
        Assert.Equal("escalate", record.Payload["action"]);
        Assert.Equal("true", record.Payload["operationalAlert"], ignoreCase: true);
        Assert.Equal("true", record.Payload["killSwitchRequested"], ignoreCase: true);
        Assert.Equal("none", record.Payload["target"]);
        Assert.DoesNotContain("Customer reports checkout failures", string.Join(" ", record.Payload.Values));
        Assert.DoesNotContain("owner@", string.Join(" ", record.Payload.Values));
    }

    private static Directive DirectiveMessage() =>
        new(
            Message,
            Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(2),
            Directive,
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");

    private static DirectiveCheckpoint Checkpoint(
        int revision,
        IEnumerable<string> completedIds,
        IEnumerable<OutcomeBlocker> blockers,
        string? nextSubtaskId)
    {
        var parentDirectiveId = DirectiveId.From(
            Guid.Parse("cccccccc-0000-0000-0000-000000001900"));
        var taskId = PositionTaskId.From(
            Guid.Parse("dddddddd-0000-0000-0000-000000001911"));
        var plan = new DirectiveCheckpointPlan(
            DirectiveCheckpointContractVersions.V1,
            [
                new DirectiveCheckpointSubtask(
                    1,
                    "inspect",
                    "Private inspection objective",
                    ["secret criterion"],
                    TimeSpan.FromMinutes(1)),
                new DirectiveCheckpointSubtask(
                    2,
                    "verify",
                    "Private verification objective",
                    ["another secret criterion"],
                    TimeSpan.FromMinutes(2)),
            ]);
        return new DirectiveCheckpoint(
            DirectiveCheckpointContractVersions.V1,
            revision,
            plan,
            new DirectiveCheckpointCorrelation(
                Organization,
                Position,
                Thread,
                Directive,
                parentDirectiveId,
                taskId),
            completedIds.Select(id => new CompletedDirectiveCheckpointSubtask(
                id,
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.PersistedState,
                    "secret.evidence")])),
            blockers,
            nextSubtaskId);
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records => _records;

        public void Append(JourneyAuditRecord record)
        {
            _records.Add(record);
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId &&
                    (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }

    private sealed class RecordingPositionProjectionPublisher : IPositionProjectionPublisher
    {
        private readonly List<PositionProjectionEvent> _events = [];

        public IReadOnlyList<PositionProjectionEvent> Events => _events;

        public void Publish(PositionProjectionEvent @event)
        {
            _events.Add(@event);
        }
    }
}
