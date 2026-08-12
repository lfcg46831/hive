using Hive.Domain.Auditing;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Auditing;

public sealed class JourneyAuditPositionProjectionPublisher : IPositionProjectionPublisher
{
    private const string TerminalResultAlreadyMaterializedReason =
        "terminal-result-already-materialized";

    private readonly IJourneyAuditLog _auditLog;
    private readonly IPositionProjectionPublisher? _inner;
    private readonly Dictionary<MessageId, DirectiveId> _directiveByMessage = new();
    private readonly Dictionary<DirectiveId, MessageId> _messageByDirective = new();
    private readonly Dictionary<MessageId, string> _messageTypeByMessage = new();

    public JourneyAuditPositionProjectionPublisher(
        IJourneyAuditLog auditLog,
        IPositionProjectionPublisher? inner = null)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _inner = inner;
    }

    public void Publish(PositionProjectionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event is PositionEventCommitted committed)
        {
            PublishCommitted(committed);
        }
        else if (@event is PositionMessageDuplicateRejected duplicate)
        {
            PublishDuplicateSuppression(duplicate);
        }
        else if (@event is PositionApprovalDecisionRejected decisionRejected)
        {
            PublishApprovalDecisionRejection(decisionRejected);
        }
        else if (@event is PositionRetainedActionLifecycleChanged lifecycle)
        {
            PublishRetainedActionLifecycle(lifecycle);
        }
        else if (@event is PositionRetainedActionReEscalationReady reEscalation)
        {
            PublishRetainedActionReEscalation(reEscalation);
        }

        _inner?.Publish(@event);
    }

    private void PublishCommitted(PositionEventCommitted committed)
    {
        switch (committed.Event)
        {
            case MessageReceived received:
                Remember(received.Message);
                _auditLog.Append(Record(
                    JourneyAuditStage.PositionAccepted,
                    committed.EntityId.Position,
                    received.Message,
                    committed.OccurredAt));
                break;

            case MessageDispatched dispatched:
                _auditLog.Append(JourneyAuditRecord.Create(
                    JourneyAuditStage.PositionDispatched,
                    JourneyAuditOutcome.Accepted,
                    committed.EntityId.Organization,
                    dispatched.Thread,
                    dispatched.Message,
                    directiveId: DirectiveFor(dispatched.Message),
                    positionId: committed.EntityId.Position,
                    messageType: MessageTypeFor(dispatched.Message),
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = nameof(PositionEventCommitted),
                        ["occupantType"] = dispatched.OccupantType.ToString(),
                        ["redactions"] = "message.payload",
                    },
                    occurredAtUtc: committed.OccurredAt));
                break;

            case DirectiveCheckpointPersisted checkpoint:
                PublishDirectiveCheckpointTransition(committed, checkpoint.Checkpoint);
                break;

            case OccupantReplyEmitted reply:
                PublishOccupantReply(committed, reply);
                break;

            case OccupantResponseTimeoutHandled timeout:
                PublishOccupantResponseTimeout(committed, timeout);
                break;
        }
    }

    private void PublishOccupantResponseTimeout(
        PositionEventCommitted committed,
        OccupantResponseTimeoutHandled timeout)
    {
        var escalation = timeout.Escalation;
        var reasonCode = escalation is null
            ? "occupant-response-timeout-no-valid-target"
            : "occupant-response-timeout-escalated";
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["scheduledForUtc"] = timeout.ScheduledFor.ToString("O"),
            ["occupantId"] = timeout.Occupant.Value,
            ["operationalAlert"] = timeout.OperationalAlert.ToString(),
            ["killSwitchRequested"] = timeout.KillSwitchRequested.ToString(),
            ["escalationMessageId"] = escalation?.Id.ToString() ?? "none",
            ["target"] = escalation is null ? "none" : EndpointValue(escalation.To),
            ["redactions"] = "sourceMessage.payload,escalation.context",
        };
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.OccupantResponseTimeout,
            escalation is null ? JourneyAuditOutcome.Failed : JourneyAuditOutcome.Accepted,
            committed.EntityId.Organization,
            timeout.Thread,
            timeout.Message,
            DirectiveFor(timeout.Message),
            committed.EntityId.Position,
            reasonCode,
            escalation is null ? nameof(OccupantResponseTimeoutHandled) : nameof(Escalation),
            payload: payload,
            occurredAtUtc: committed.OccurredAt,
            idempotencyDiscriminator: $"{timeout.Message}:{timeout.ScheduledFor:O}:{reasonCode}"));
    }

    private void PublishOccupantReply(
        PositionEventCommitted committed,
        OccupantReplyEmitted reply)
    {
        // AI results are first persisted as a durable handoff/outbox fact. Their canonical
        // ResultMessageCreated audit is appended by the AI adapter only after the destination
        // PositionActor confirms MessageReceived. Human/external replies retain the legacy path.
        if (reply.Author.Kind == OccupantReplyAuthorKind.AiAgent)
        {
            return;
        }

        Remember(reply.Message);
        var directiveId = reply.Message switch
        {
            Report report => report.AboutDirectiveId,
            Directive directive => directive.DirectiveId,
            _ => null,
        };
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.ResultMessageCreated,
            JourneyAuditOutcome.Succeeded,
            committed.EntityId.Organization,
            reply.Message.Thread,
            reply.SourceMessageId,
            directiveId,
            committed.EntityId.Position,
            reasonCode: "occupant-reply-emitted",
            messageType: reply.Message.GetType().Name,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = reply.Author.Channel,
                ["authorKind"] = OccupantReplyAuthorKindContract.ToWireValue(
                    reply.Author.Kind),
                ["authorSubjectId"] = reply.Author.SubjectId,
                ["resultMessageId"] = reply.Message.Id.ToString(),
                ["channel"] = reply.Message.Channel.ToString(),
                ["redactions"] = "message.payload",
            },
            occurredAtUtc: committed.OccurredAt,
            idempotencyDiscriminator: reply.Message.Id.ToString()));
    }

    private void PublishDirectiveCheckpointTransition(
        PositionEventCommitted committed,
        DirectiveCheckpoint checkpoint)
    {
        var transition = checkpoint.Revision == 1 ? "created" : "advanced";
        var reasonCode = $"checkpoint-{transition}";
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.DirectiveCheckpointTransition,
            JourneyAuditOutcome.Succeeded,
            checkpoint.Correlation.OrganizationId,
            checkpoint.Correlation.ThreadId,
            MessageFor(
                checkpoint.Correlation.ThreadId,
                checkpoint.Correlation.DirectiveId),
            checkpoint.Correlation.DirectiveId,
            committed.EntityId.Position,
            reasonCode,
            nameof(DirectiveCheckpointPersisted),
            payload: CheckpointPayload(checkpoint, transition),
            occurredAtUtc: committed.OccurredAt,
            idempotencyDiscriminator:
                $"{checkpoint.Correlation.DirectiveId}:{checkpoint.Revision}:{transition}"));
    }

    private static IReadOnlyDictionary<string, string> CheckpointPayload(
        DirectiveCheckpoint checkpoint,
        string transition) =>
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["transition"] = transition,
            ["contractVersion"] = checkpoint.ContractVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["planContractVersion"] = checkpoint.Plan.ContractVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["revision"] = checkpoint.Revision.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["plannedSubtaskCount"] = checkpoint.Plan.Subtasks.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["completedSubtaskCount"] = checkpoint.CompletedSubtasks.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["completedSubtaskIds"] = string.Join(
                ",",
                checkpoint.CompletedSubtasks.Select(completed => completed.LocalId)),
            ["blockerCount"] = checkpoint.Blockers.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["blockerCodes"] = string.Join(
                ",",
                checkpoint.Blockers.Select(OutcomeBlockerContract.ToWireValue)),
            ["nextSubtaskId"] = checkpoint.NextSubtaskId ?? "none",
            ["parentDirectiveId"] = checkpoint.Correlation.ParentDirectiveId?.ToString() ?? "none",
            ["positionTaskId"] = checkpoint.Correlation.PositionTaskId?.ToString() ?? "none",
            ["redactions"] =
                "plan.objectives,plan.completionCriteria,plan.estimates,evidence.references,report.body,prompt,provider.output,reasoning",
        };

    private void PublishDuplicateSuppression(PositionMessageDuplicateRejected duplicate)
    {
        var terminalResult = _auditLog
            .ReadByThread(duplicate.Thread)
            .Where(record =>
                record.OrganizationId == duplicate.EntityId.Organization
                && record.MessageId == duplicate.Message
                && record.PositionId == duplicate.EntityId.Position)
            .LastOrDefault(record => record.Stage == JourneyAuditStage.ResultMessageCreated);
        if (terminalResult is null)
        {
            return;
        }

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.DuplicateSuppressed,
            JourneyAuditOutcome.Rejected,
            duplicate.EntityId.Organization,
            duplicate.Thread,
            duplicate.Message,
            terminalResult.DirectiveId,
            duplicate.EntityId.Position,
            reasonCode: TerminalResultAlreadyMaterializedReason,
            messageType: terminalResult.MessageType,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suppressedStage"] = terminalResult.Stage.ToString(),
                ["suppressedOutcome"] = terminalResult.Outcome.ToString(),
                ["reasonCode"] = TerminalResultAlreadyMaterializedReason,
                ["redactions"] = "directive.objective,directive.context,gateway.request.content,gateway.response.text",
            },
            occurredAtUtc: duplicate.OccurredAt,
            idempotencyDiscriminator: TerminalResultAlreadyMaterializedReason));
    }

    private void PublishApprovalDecisionRejection(
        PositionApprovalDecisionRejected rejected)
    {
        var audit = RoutingRejectionAuditEvent.FromRejection(
            rejected.Rejection,
            rejected.OccurredAt);
        var errors = audit.Errors
            .Select(error =>
                $"{error.Code}@{error.Path}:{RejectionReasonContract.ToWireValue(error.Reason)}")
            .ToArray();
        var reasons = audit.Reasons
            .Select(RejectionReasonContract.ToWireValue)
            .ToArray();
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.PositionAccepted,
            JourneyAuditOutcome.Rejected,
            audit.OrganizationId,
            audit.Thread,
            audit.MessageId,
            positionId: rejected.EntityId.Position,
            reasonCode: audit.Errors[0].Code,
            messageType: nameof(ApprovalDecision),
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = rejected.Author.Channel,
                ["authorKind"] = OccupantReplyAuthorKindContract.ToWireValue(
                    rejected.Author.Kind),
                ["authorSubjectId"] = rejected.Author.SubjectId,
                ["requestMessageId"] = rejected.RequestId.ToString(),
                ["sender"] = EndpointValue(audit.Sender),
                ["recipient"] = EndpointValue(audit.Recipient),
                ["expectedApprover"] = audit.ExpectedApprover is null
                    ? "unknown"
                    : EndpointValue(audit.ExpectedApprover),
                ["receivedPolicy"] = audit.ReceivedPolicy?.Value ?? "unknown",
                ["expectedPolicyVersion"] = audit.ExpectedPolicyVersion?.ToString() ?? "unknown",
                ["errors"] = string.Join(",", errors),
                ["rejectionReasons"] = string.Join(",", reasons),
                ["redactions"] = "reason",
            },
            occurredAtUtc: rejected.OccurredAt,
            idempotencyDiscriminator:
                $"{rejected.RequestId}:{string.Join(',', errors)}"));
    }

    private static string EndpointValue(EndpointRef endpoint) =>
        endpoint switch
        {
            PositionEndpointRef position => $"position:{position.PositionId.Value}",
            OrganizationOwnerEndpointRef => "organization-owner",
            SystemEndpointRef system => $"system:{system.Kind}",
            _ => endpoint.GetType().Name,
        };

    private void PublishRetainedActionLifecycle(PositionRetainedActionLifecycleChanged lifecycle)
    {
        var action = lifecycle.Action;
        var resolution = Resolution(action, lifecycle.Transition);
        var (outcome, code) = lifecycle.Transition switch
        {
            RetainedActionAuthorized => (JourneyAuditOutcome.Accepted, "authorization-grant-accepted"),
            RetainedActionDenied => (JourneyAuditOutcome.Accepted, "authorization-denial-accepted"),
            RetainedActionConsumed => (JourneyAuditOutcome.Succeeded, "retained-action-consumed"),
            RetainedActionExpired => (JourneyAuditOutcome.Rejected, action.ReEscalationCode!),
            RetainedActionReturned => (JourneyAuditOutcome.Rejected, action.ReEscalationCode!),
            _ => throw new ArgumentOutOfRangeException(nameof(lifecycle)),
        };

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.RetainedActionLifecycle,
            outcome,
            action.OrganizationId,
            action.ThreadId,
            resolution.Id,
            action.DirectiveId,
            action.PositionId,
            code,
            lifecycle.Transition.GetType().Name,
            payload: LifecyclePayload(action, resolution, lifecycle.Transition),
            occurredAtUtc: lifecycle.OccurredAt,
            idempotencyDiscriminator:
                $"{action.Id}:{resolution.Id}:{lifecycle.Transition.GetType().Name}:{code}"));
    }

    private void PublishRetainedActionReEscalation(
        PositionRetainedActionReEscalationReady reEscalation)
    {
        var action = reEscalation.Action;
        var grant = action.AuthorizationGrant
            ?? throw new InvalidOperationException("A re-escalated retained action must preserve its grant.");
        var code = action.ReEscalationCode
            ?? throw new InvalidOperationException("A re-escalated retained action must preserve its code.");

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.RetainedActionReEscalation,
            JourneyAuditOutcome.Accepted,
            action.OrganizationId,
            action.ThreadId,
            grant.Id,
            action.DirectiveId,
            action.PositionId,
            code,
            reEscalation.Transition.GetType().Name,
            payload: LifecyclePayload(action, grant, reEscalation.Transition),
            occurredAtUtc: reEscalation.OccurredAt,
            idempotencyDiscriminator:
                $"{action.Id}:{grant.Id}:{reEscalation.Transition.GetType().Name}:{code}"));
    }

    private static OrgMessage Resolution(PersistedRetainedAction action, PositionEvent transition) =>
        transition switch
        {
            RetainedActionAuthorized authorized => authorized.Grant,
            RetainedActionDenied denied => denied.Denial,
            _ => action.AuthorizationGrant
                ?? throw new InvalidOperationException(
                    "An authorized retained-action transition must preserve its grant."),
        };

    private static IReadOnlyDictionary<string, string> LifecyclePayload(
        PersistedRetainedAction action,
        OrgMessage resolution,
        PositionEvent transition)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["retainedActionId"] = action.Id.ToString(),
            ["resolutionType"] = resolution.GetType().Name,
            ["resolutionMessageId"] = resolution.Id.ToString(),
            ["approvalPolicyRefs"] = string.Join(",", action.ApprovalPolicies.Select(item => item.Value)),
            ["state"] = action.State.ToString(),
            ["transition"] = transition.GetType().Name,
            ["redactions"] = "reason,fingerprint,canonicalPayload,canonicalFacts,governanceMessages",
        };
        if (resolution is AuthorizationGrant grant)
        {
            payload["grantId"] = grant.Id.ToString();
            payload["authorityKey"] = grant.Key.Value;
        }

        return payload;
    }

    private void Remember(OrgMessage message)
    {
        _messageTypeByMessage[message.Id] = message.GetType().Name;
        if (message is Directive directive)
        {
            _directiveByMessage[message.Id] = directive.DirectiveId;
            _messageByDirective[directive.DirectiveId] = message.Id;
        }
        else if (message is Report report)
        {
            _directiveByMessage[message.Id] = report.AboutDirectiveId;
        }
    }

    private JourneyAuditRecord Record(
        JourneyAuditStage stage,
        PositionId positionId,
        OrgMessage message,
        DateTimeOffset occurredAt) =>
        JourneyAuditRecord.Create(
            stage,
            JourneyAuditOutcome.Accepted,
            message.OrganizationId,
            message.Thread,
            message.Id,
            directiveId: DirectiveFor(message.Id),
            positionId: positionId,
            messageType: message.GetType().Name,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = nameof(PositionEventCommitted),
                ["channel"] = message.Channel.ToString(),
                ["priority"] = message.Priority.ToString(),
                ["redactions"] = "message.payload",
            },
            occurredAtUtc: occurredAt);

    private DirectiveId? DirectiveFor(MessageId message) =>
        _directiveByMessage.TryGetValue(message, out var directiveId)
            ? directiveId
            : null;

    private MessageId MessageFor(ThreadId threadId, DirectiveId directiveId)
    {
        if (_messageByDirective.TryGetValue(directiveId, out var messageId))
        {
            return messageId;
        }

        var accepted = _auditLog
            .ReadByThread(threadId, directiveId)
            .LastOrDefault(record => record.Stage == JourneyAuditStage.PositionAccepted);
        return accepted?.MessageId ?? MessageId.From(directiveId.Value);
    }

    private string? MessageTypeFor(MessageId message) =>
        _messageTypeByMessage.TryGetValue(message, out var messageType)
            ? messageType
            : null;
}
