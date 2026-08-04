using System.Text;
using System.Text.Json;
using Hive.Actors.Serialization;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Actors.Inbox;

/// <summary>
/// Deterministically folds persisted inbox facts into one item per recipient position and message.
/// Durable application, replay idempotency and projection watermarks belong to US-F1-02-T03c.
/// </summary>
internal sealed class InboxProjectionFactMapper
{
    internal const string DeadlineExpiredFactType = "deadline-expired";

    private readonly Dictionary<InboxProjectionItemKey, TrackedInboxItem> _items = [];
    private readonly Dictionary<InboxProjectionItemKey, InboxResponseEvidence> _responseEvidence = [];
    private readonly TimeProvider _timeProvider;

    public InboxProjectionFactMapper(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public InboxProjectionItem? CurrentItem(
        OrganizationId organizationId,
        PositionId assignedPositionId,
        MessageId messageId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(assignedPositionId);
        ArgumentNullException.ThrowIfNull(messageId);
        return _items.TryGetValue(
            new InboxProjectionItemKey(organizationId, assignedPositionId, messageId),
            out var tracked)
            ? tracked.Item
            : null;
    }

    public IReadOnlyList<InboxProjectionChange> Apply(InboxProjectionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var changes = new List<InboxProjectionChange>();

        switch (fact.Source)
        {
            case InboxProjectionSource.PositionEvent:
                IgnorePositionEvent(fact);
                break;
            case InboxProjectionSource.OrganizationalMessage:
                ApplyMessage(fact, changes);
                break;
            case InboxProjectionSource.AuditLog:
                ApplyAuditFact(fact, changes);
                break;
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(fact),
                    fact.Source,
                    "Unknown inbox projection source.");
        }

        ExpireDueItems(changes);
        return changes;
    }

    public IReadOnlyList<InboxProjectionChange> RefreshExpirations()
    {
        var changes = new List<InboxProjectionChange>();
        ExpireDueItems(changes);
        return changes;
    }

    private static void IgnorePositionEvent(InboxProjectionFact fact)
    {
        var @event = (PositionEvent)PositionProtocolJsonFormat.Deserialize(
            fact.FactType,
            Encoding.UTF8.GetBytes(fact.PayloadJson));

        _ = @event switch
        {
            // MessageReceived is represented by the separate OrganizationalMessage fact emitted by
            // T03a. The remaining events describe actor processing, task, occupant, configuration,
            // retained-action, checkpoint or passivation lifecycles and do not change human inbox
            // derivations.
            MessageReceived or MessageDispatched or MessageProcessingCompleted
                or TaskCreated or TaskUpdated or TaskCompleted or ShortMemoryUpdated
                or OccupantChanged or PositionPassivated or PositionConfigurationApplied
                or ActionRetained or RetainedActionAuthorized or RetainedActionDenied
                or RetainedActionConsumed or RetainedActionExpired or RetainedActionReturned
                or DirectiveCheckpointPersisted => true,
            _ => throw new InvalidOperationException(
                $"Position event '{@event.GetType().Name}' has no explicit inbox mapping."),
        };
    }

    private void ApplyMessage(
        InboxProjectionFact fact,
        ICollection<InboxProjectionChange> changes)
    {
        var message = (OrgMessage)OrgMessageJsonFormat.Deserialize(
            fact.FactType,
            Encoding.UTF8.GetBytes(fact.PayloadJson));
        ValidateMessageFact(fact, message);

        switch (message)
        {
            case Directive or Report or Escalation or Memo or PeerRequest or PeerResponse
                or ApprovalRequest or ApprovalDecision:
                AddInboxItem(fact, message, changes);
                ApplyCorrelations(message, fact.OccurredAtUtc, changes);
                break;

            // Authorization resolutions belong to the retained-action lifecycle (§9.11), while
            // scheduler pulses and domain event triggers belong to system/proactive processing.
            // None is a public human-inbox item in US-F1-02-T01.
            case AuthorizationGrant or AuthorizationDenial or Pulse or EventTrigger:
                break;

            default:
                throw new InvalidOperationException(
                    $"Organizational message '{message.GetType().Name}' has no explicit inbox mapping.");
        }
    }

    private void ApplyAuditFact(
        InboxProjectionFact fact,
        ICollection<InboxProjectionChange> changes)
    {
        switch (fact.FactType)
        {
            case nameof(JourneyAuditStage.ResultMessageCreated):
                ApplyResultMessageCreated(fact, changes);
                break;

            case nameof(JourneyAuditStage.SubmissionReceived)
                or nameof(JourneyAuditStage.DirectiveCreated)
                or nameof(JourneyAuditStage.PositionAccepted)
                or nameof(JourneyAuditStage.PositionDispatched)
                or nameof(JourneyAuditStage.GatewayCalled)
                or nameof(JourneyAuditStage.AgentDecided)
                or nameof(JourneyAuditStage.GatewayCostRecorded)
                or nameof(JourneyAuditStage.DuplicateSuppressed)
                or nameof(JourneyAuditStage.ActionGateEvaluated)
                or nameof(JourneyAuditStage.RetainedActionResume)
                or nameof(JourneyAuditStage.AuthorizationResolution)
                or nameof(JourneyAuditStage.RetainedActionLifecycle)
                or nameof(JourneyAuditStage.RetainedActionReEscalation)
                or nameof(JourneyAuditStage.OutcomeResolved)
                or nameof(JourneyAuditStage.DirectiveCheckpointTransition):
                break;

            default:
                throw new InvalidOperationException(
                    $"Audit fact '{fact.FactType}' has no explicit inbox mapping.");
        }
    }

    private void ApplyResultMessageCreated(
        InboxProjectionFact fact,
        ICollection<InboxProjectionChange> changes)
    {
        using var payload = JsonDocument.Parse(fact.PayloadJson);
        var outcome = RequiredAuditText(payload.RootElement, "outcome", fact);
        if (outcome is nameof(JourneyAuditOutcome.Accepted)
            or nameof(JourneyAuditOutcome.Rejected)
            or nameof(JourneyAuditOutcome.Failed))
        {
            return;
        }

        if (outcome != nameof(JourneyAuditOutcome.Succeeded))
        {
            throw new InvalidOperationException(
                $"Audit fact '{fact.FactType}' has unknown outcome '{outcome}'.");
        }

        // A successful ResultMessageCreated/Report is the durable response evidence when the
        // report has no PositionActor recipient (for example, OrganizationOwner). Other result
        // message types do not satisfy the closed Directive -> Report response mapping of T07.
        if (!string.Equals(
                RequiredAuditText(payload.RootElement, "message_type", fact),
                nameof(Report),
                StringComparison.Ordinal))
        {
            return;
        }

        var key = new InboxProjectionItemKey(
            fact.OrganizationId,
            fact.PositionId
                ?? throw new InvalidOperationException(
                    $"Audit fact '{fact.FactType}' has no responding position."),
            fact.MessageId
                ?? throw new InvalidOperationException(
                    $"Audit fact '{fact.FactType}' has no source message identifier."));
        var evidence = new InboxResponseEvidence(
            fact.FactType,
            fact.ThreadId
                ?? throw new InvalidOperationException(
                    $"Audit fact '{fact.FactType}' has no source thread identifier."),
            fact.OccurredAtUtc);
        _responseEvidence[key] = evidence;
        if (!_items.TryGetValue(key, out var tracked))
        {
            return;
        }

        if (tracked.Message is not Directive || tracked.Message.Thread != fact.ThreadId)
        {
            throw new InvalidOperationException(
                $"Audit response fact '{fact.FactType}' does not identify its source directive item.");
        }

        if (tracked.Item.ResponseState != InboxProjectionResponseState.Responded)
        {
            Replace(
                tracked,
                tracked.Item with { ResponseState = InboxProjectionResponseState.Responded },
                evidence.FactType,
                evidence.OccurredAtUtc,
                changes);
        }
    }

    private static string RequiredAuditText(
        JsonElement payload,
        string propertyName,
        InboxProjectionFact fact)
    {
        if (!payload.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(property.GetString()))
        {
            throw new InvalidOperationException(
                $"Audit fact '{fact.FactType}' has no valid '{propertyName}' value.");
        }

        return property.GetString()!;
    }

    private void AddInboxItem(
        InboxProjectionFact fact,
        OrgMessage message,
        ICollection<InboxProjectionChange> changes)
    {
        var assignedPositionId = fact.PositionId
            ?? throw new InvalidOperationException(
                $"Organizational message fact '{fact.FactType}' has no recipient position.");
        var key = new InboxProjectionItemKey(
            message.OrganizationId,
            assignedPositionId,
            message.Id);
        var responseState = InitialResponseState(message);
        if (message is Directive && _responseEvidence.TryGetValue(key, out var evidence))
        {
            if (evidence.ThreadId != message.Thread)
            {
                throw new InvalidOperationException(
                    $"Audit response fact '{evidence.FactType}' does not match source directive '{message.Id}'.");
            }

            responseState = InboxProjectionResponseState.Responded;
        }

        var item = new InboxProjectionItem(
            key,
            MessageType(message),
            message.From,
            message.To,
            message.Thread,
            message.Priority,
            message.SentAt.ToUniversalTime(),
            message.Deadline?.ToUniversalTime(),
            IsExpired(message.Deadline),
            responseState,
            ApprovalMetadata(message));

        if (!_items.TryAdd(key, new TrackedInboxItem(message, item)))
        {
            throw new InvalidOperationException(
                $"Inbox item '{key}' was mapped more than once before replay handling was applied.");
        }

        changes.Add(new InboxProjectionChange(
            item,
            fact.FactType,
            fact.OccurredAtUtc));
    }

    private void ApplyCorrelations(
        OrgMessage message,
        DateTimeOffset occurredAtUtc,
        ICollection<InboxProjectionChange> changes)
    {
        switch (message)
        {
            case Report report:
                MarkResponded(
                    tracked => tracked.Message is Directive directive
                        && directive.DirectiveId == report.AboutDirectiveId
                        && IsCorrelatedResponse(directive, report),
                    report,
                    occurredAtUtc,
                    changes);
                break;

            case PeerResponse response:
                MarkResponded(
                    tracked => tracked.Message is PeerRequest request
                        && request.Id == response.InReplyTo
                        && IsCorrelatedResponse(request, response),
                    response,
                    occurredAtUtc,
                    changes);
                break;

            case Directive directive:
                MarkResponded(
                    tracked => tracked.Message is Escalation escalation
                        && IsCorrelatedResponse(escalation, directive),
                    directive,
                    occurredAtUtc,
                    changes);
                break;

            case ApprovalDecision decision:
                MarkDecisionIssued(decision, occurredAtUtc, changes);
                break;
        }
    }

    private void MarkResponded(
        Func<TrackedInboxItem, bool> predicate,
        OrgMessage response,
        DateTimeOffset occurredAtUtc,
        ICollection<InboxProjectionChange> changes)
    {
        foreach (var tracked in _items.Values
                     .Where(predicate)
                     .OrderBy(item => item.Item.Key.OrganizationId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Item.Key.AssignedPositionId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Item.Key.MessageId.Value))
        {
            if (tracked.Item.ResponseState == InboxProjectionResponseState.Responded)
            {
                continue;
            }

            Replace(
                tracked,
                tracked.Item with { ResponseState = InboxProjectionResponseState.Responded },
                response.GetType().Name,
                occurredAtUtc,
                changes);
        }
    }

    private void MarkDecisionIssued(
        ApprovalDecision decision,
        DateTimeOffset occurredAtUtc,
        ICollection<InboxProjectionChange> changes)
    {
        var tracked = ApprovalRequestFor(decision);
        var approval = tracked.Item.Approval
            ?? throw new InvalidOperationException(
                $"Approval request inbox item '{tracked.Item.Key}' has no approval metadata.");
        var state = decision.Approved
            ? InboxProjectionApprovalState.Approved
            : InboxProjectionApprovalState.Rejected;
        var updated = tracked.Item with
        {
            Approval = approval with
            {
                State = state,
                DecisionMessageId = decision.Id,
                DecidedAtUtc = decision.SentAt.ToUniversalTime(),
            },
        };
        Replace(
            tracked,
            updated,
            decision.GetType().Name,
            occurredAtUtc,
            changes);
    }

    private InboxProjectionApproval? ApprovalMetadata(OrgMessage message) =>
        message switch
        {
            ApprovalRequest request => new InboxProjectionApproval(
                request.Id,
                request.Action,
                request.Policy,
                IsExpired(request.Deadline)
                    ? InboxProjectionApprovalState.Expired
                    : InboxProjectionApprovalState.Pending,
                DecisionMessageId: null,
                DecidedAtUtc: null),
            ApprovalDecision decision => DecisionMetadata(decision),
            _ => null,
        };

    private InboxProjectionApproval DecisionMetadata(ApprovalDecision decision)
    {
        var request = ApprovalRequestFor(decision).Message as ApprovalRequest
            ?? throw new InvalidOperationException(
                $"Approval decision '{decision.Id}' does not correlate to an approval request.");
        return new InboxProjectionApproval(
            request.Id,
            request.Action,
            request.Policy,
            decision.Approved
                ? InboxProjectionApprovalState.Approved
                : InboxProjectionApprovalState.Rejected,
            decision.Id,
            decision.SentAt.ToUniversalTime());
    }

    private TrackedInboxItem ApprovalRequestFor(ApprovalDecision decision)
    {
        var matches = _items.Values
            .Where(tracked => tracked.Message is ApprovalRequest request
                && request.OrganizationId == decision.OrganizationId
                && request.Id == decision.RequestId)
            .ToArray();
        if (matches.Length != 1 ||
            matches[0].Message is not ApprovalRequest request ||
            !IsCorrelatedResponse(request, decision))
        {
            throw new InvalidOperationException(
                $"Approval decision '{decision.Id}' has no single correlated accepted request '{decision.RequestId}'.");
        }

        return matches[0];
    }

    private void ExpireDueItems(ICollection<InboxProjectionChange> changes)
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var tracked in _items.Values
                     .Where(item => !item.Item.IsExpired
                         && item.Item.DeadlineAtUtc is { } deadline
                         && deadline <= now)
                     .OrderBy(item => item.Item.Key.OrganizationId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Item.Key.AssignedPositionId.Value, StringComparer.Ordinal)
                     .ThenBy(item => item.Item.Key.MessageId.Value))
        {
            var approval = tracked.Item.Approval is { State: InboxProjectionApprovalState.Pending } pending
                ? pending with { State = InboxProjectionApprovalState.Expired }
                : tracked.Item.Approval;
            Replace(
                tracked,
                tracked.Item with { IsExpired = true, Approval = approval },
                DeadlineExpiredFactType,
                now,
                changes);
        }
    }

    private void Replace(
        TrackedInboxItem tracked,
        InboxProjectionItem item,
        string factType,
        DateTimeOffset occurredAtUtc,
        ICollection<InboxProjectionChange> changes)
    {
        tracked.Item = item;
        changes.Add(new InboxProjectionChange(
            item,
            factType,
            occurredAtUtc.ToUniversalTime()));
    }

    private bool IsExpired(DateTimeOffset? deadline) =>
        deadline is { } value && value <= _timeProvider.GetUtcNow();

    private static bool IsCorrelatedResponse(OrgMessage request, OrgMessage response) =>
        request.OrganizationId == response.OrganizationId
        && request.Thread == response.Thread
        && request.From == response.To
        && request.To == response.From;

    private static void ValidateMessageFact(InboxProjectionFact fact, OrgMessage message)
    {
        if (message.OrganizationId != fact.OrganizationId ||
            message.Id != fact.MessageId ||
            message.Thread != fact.ThreadId)
        {
            throw new InvalidOperationException(
                $"Organizational message '{message.Id}' does not match its captured inbox fact identity.");
        }

        if (message.To is not PositionEndpointRef destination ||
            destination.PositionId != fact.PositionId)
        {
            throw new InvalidOperationException(
                $"Organizational message '{message.Id}' was not captured for its destination position.");
        }
    }

    private static InboxProjectionMessageType MessageType(OrgMessage message) =>
        message switch
        {
            Directive => InboxProjectionMessageType.Directive,
            Report => InboxProjectionMessageType.Report,
            Escalation => InboxProjectionMessageType.Escalation,
            Memo => InboxProjectionMessageType.Memo,
            PeerRequest => InboxProjectionMessageType.PeerRequest,
            PeerResponse => InboxProjectionMessageType.PeerResponse,
            ApprovalRequest => InboxProjectionMessageType.ApprovalRequest,
            ApprovalDecision => InboxProjectionMessageType.ApprovalDecision,
            _ => throw new InvalidOperationException(
                $"Organizational message '{message.GetType().Name}' is not a human inbox item."),
        };

    private static InboxProjectionResponseState InitialResponseState(OrgMessage message) =>
        message switch
        {
            Directive or Escalation or PeerRequest =>
                InboxProjectionResponseState.AwaitingResponse,
            Report or Memo or PeerResponse or ApprovalRequest or ApprovalDecision =>
                InboxProjectionResponseState.NotApplicable,
            _ => throw new InvalidOperationException(
                $"Organizational message '{message.GetType().Name}' has no inbox response state."),
        };

    private sealed class TrackedInboxItem(OrgMessage message, InboxProjectionItem item)
    {
        public OrgMessage Message { get; } = message;

        public InboxProjectionItem Item { get; set; } = item;
    }

    private sealed record InboxResponseEvidence(
        string FactType,
        ThreadId ThreadId,
        DateTimeOffset OccurredAtUtc);
}

internal readonly record struct InboxProjectionItemKey(
    OrganizationId OrganizationId,
    PositionId AssignedPositionId,
    MessageId MessageId)
{
    public override string ToString() =>
        $"{OrganizationId.Value}/{AssignedPositionId.Value}/{MessageId}";
}

internal enum InboxProjectionMessageType
{
    Directive,
    Report,
    Escalation,
    Memo,
    PeerRequest,
    PeerResponse,
    ApprovalRequest,
    ApprovalDecision,
}

internal enum InboxProjectionResponseState
{
    NotApplicable,
    AwaitingResponse,
    Responded,
}

internal enum InboxProjectionApprovalState
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

internal sealed record InboxProjectionApproval(
    MessageId RequestId,
    string Action,
    ApprovalPolicyRef Policy,
    InboxProjectionApprovalState State,
    MessageId? DecisionMessageId,
    DateTimeOffset? DecidedAtUtc);

internal sealed record InboxProjectionItem(
    InboxProjectionItemKey Key,
    InboxProjectionMessageType Type,
    EndpointRef Origin,
    EndpointRef Destination,
    ThreadId ThreadId,
    Priority Priority,
    DateTimeOffset SentAtUtc,
    DateTimeOffset? DeadlineAtUtc,
    bool IsExpired,
    InboxProjectionResponseState ResponseState,
    InboxProjectionApproval? Approval);

internal sealed record InboxProjectionChange(
    InboxProjectionItem Item,
    string FactType,
    DateTimeOffset OccurredAtUtc);
