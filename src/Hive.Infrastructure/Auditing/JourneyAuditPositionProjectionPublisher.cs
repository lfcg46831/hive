using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Auditing;

public sealed class JourneyAuditPositionProjectionPublisher : IPositionProjectionPublisher
{
    private const string TerminalResultAlreadyMaterializedReason =
        "terminal-result-already-materialized";

    private readonly IJourneyAuditLog _auditLog;
    private readonly IPositionProjectionPublisher? _inner;
    private readonly Dictionary<MessageId, DirectiveId> _directiveByMessage = new();
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
        }
    }

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

    private string? MessageTypeFor(MessageId message) =>
        _messageTypeByMessage.TryGetValue(message, out var messageType)
            ? messageType
            : null;
}
