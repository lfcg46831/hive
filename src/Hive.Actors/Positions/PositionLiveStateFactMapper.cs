using System.Text;
using Hive.Actors.Serialization;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Actors.Positions;

/// <summary>
/// Deterministically folds persisted position facts into the canonical live-state indicators of
/// US-F1-01-T06b. The durable projection worker checkpoints this fold and reconstructs it during
/// replay without publishing historical transitions again.
/// </summary>
internal sealed class PositionLiveStateFactMapper
{
    private readonly Dictionary<string, PositionIndicators> _positions =
        new(StringComparer.Ordinal);

    public PositionLiveState CurrentState(PositionEntityId entityId)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        return _positions.TryGetValue(entityId.Value, out var indicators)
            ? indicators.Evaluate()
            : PositionLiveState.Idle;
    }

    public PositionLiveStateTransition? Apply(PositionLiveStateProjectionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);

        return fact.Source switch
        {
            PositionLiveStateProjectionSource.PositionEvent => ApplyPositionEvent(fact),
            PositionLiveStateProjectionSource.OrganizationalMessage => ApplyMessage(fact),
            PositionLiveStateProjectionSource.AuditLog => IgnoreKnownAuditFact(fact),
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact.Source, "Unknown projection source."),
        };
    }

    /// <summary>
    /// Applies an operational condition derived from a persisted configuration/operations source.
    /// This keeps the precedence rule in one place while the source-specific subscription remains
    /// outside T06b.
    /// </summary>
    public PositionLiveStateTransition? Apply(PositionLiveStateConditionFact fact)
    {
        ArgumentNullException.ThrowIfNull(fact);
        var indicators = Indicators(fact.EntityId);
        var previous = indicators.Evaluate();
        var changed = fact.Condition switch
        {
            PositionLiveStateCondition.OutsideWorkingHours =>
                Set(ref indicators.OutsideWorkingHours, fact.IsActive),
            PositionLiveStateCondition.KillSwitch =>
                Set(ref indicators.KillSwitchActive, fact.IsActive),
            PositionLiveStateCondition.ConfigurationBlocked =>
                Set(ref indicators.ConfigurationBlocked, fact.IsActive),
            _ => throw new ArgumentOutOfRangeException(nameof(fact), fact.Condition, "Unknown live-state condition."),
        };

        return changed
            ? Transition(
                fact.EntityId,
                indicators,
                fact.EventType,
                fact.OccurredAtUtc,
                threadId: null,
                previous)
            : null;
    }

    private PositionLiveStateTransition? ApplyPositionEvent(PositionLiveStateProjectionFact fact)
    {
        var entityId = FactEntity(fact);
        var @event = (PositionEvent)PositionProtocolJsonFormat.Deserialize(
            fact.FactType,
            Encoding.UTF8.GetBytes(fact.PayloadJson));

        return @event switch
        {
            TaskCreated created => AddTask(entityId, created),
            TaskCompleted completed => CompleteTask(entityId, completed),
            MessageDispatched dispatched => StartProcessing(entityId, dispatched),
            MessageProcessingCompleted completed => CompleteProcessing(entityId, completed),
            ActionRetained retained => RetainAction(entityId, retained),
            RetainedActionAuthorized authorized => ObserveAuthorizedAction(entityId, authorized),
            RetainedActionDenied denied => CloseRetainedAction(entityId, denied.Denial.RetainedActionId, denied),
            RetainedActionConsumed consumed => CloseRetainedAction(entityId, consumed.ActionId, consumed),
            RetainedActionExpired expired => CloseRetainedAction(entityId, expired.ActionId, expired),
            RetainedActionReturned returned => ObserveReturnedAction(entityId, returned),
            PositionConfigurationApplied applied => ApplyConfigurationReady(entityId, applied),

            // These facts do not change any canonical operational-state indicator. MessageReceived
            // is deliberately ignored here because its embedded message is captured as a separate
            // OrganizationalMessage fact by T06a.
            MessageReceived or TaskUpdated or ShortMemoryUpdated or OccupantChanged
                or PositionPassivated or DirectiveCheckpointPersisted
                or OccupantReplyEmitted => null,
            _ => throw new InvalidOperationException(
                $"Position event '{@event.GetType().Name}' has no explicit live-state mapping."),
        };
    }

    private PositionLiveStateTransition? ApplyMessage(PositionLiveStateProjectionFact fact)
    {
        var message = (OrgMessage)OrgMessageJsonFormat.Deserialize(
            fact.FactType,
            Encoding.UTF8.GetBytes(fact.PayloadJson));
        if (message.OrganizationId != fact.OrganizationId)
        {
            throw new InvalidOperationException(
                $"Organizational message '{message.Id}' does not belong to projection organization '{fact.OrganizationId}'.");
        }

        return message switch
        {
            Escalation escalation => OpenEscalation(escalation),
            Directive directive => ResolveEscalation(directive),
            ApprovalRequest request => OpenApproval(request),
            ApprovalDecision decision => ResolveApproval(decision),
            AuthorizationGrant grant => ResolveEscalation(grant),
            AuthorizationDenial denial => ResolveEscalation(denial),

            // Report intentionally has no Blocked semantics (§9.5). The remaining message kinds
            // may drive processing through MessageDispatched, but receipt alone is not Working.
            Report or Memo or PeerRequest or PeerResponse or Pulse or EventTrigger => null,
            _ => throw new InvalidOperationException(
                $"Organizational message '{message.GetType().Name}' has no explicit live-state mapping."),
        };
    }

    private static PositionLiveStateTransition? IgnoreKnownAuditFact(
        PositionLiveStateProjectionFact fact) =>
        fact.FactType switch
        {
            nameof(JourneyAuditStage.SubmissionReceived)
                or nameof(JourneyAuditStage.DirectiveCreated)
                or nameof(JourneyAuditStage.PositionAccepted)
                or nameof(JourneyAuditStage.PositionDispatched)
                or nameof(JourneyAuditStage.GatewayCalled)
                or nameof(JourneyAuditStage.AgentDecided)
                or nameof(JourneyAuditStage.ResultMessageCreated)
                or nameof(JourneyAuditStage.GatewayCostRecorded)
                or nameof(JourneyAuditStage.DuplicateSuppressed)
                or nameof(JourneyAuditStage.ActionGateEvaluated)
                or nameof(JourneyAuditStage.RetainedActionResume)
                or nameof(JourneyAuditStage.AuthorizationResolution)
                or nameof(JourneyAuditStage.RetainedActionLifecycle)
                or nameof(JourneyAuditStage.RetainedActionReEscalation)
                or nameof(JourneyAuditStage.OutcomeResolved)
                or nameof(JourneyAuditStage.DirectiveCheckpointTransition) => null,
            _ => throw new InvalidOperationException(
                $"Audit fact '{fact.FactType}' has no explicit live-state mapping."),
        };

    private PositionLiveStateTransition? AddTask(PositionEntityId entityId, TaskCreated @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.OpenTasks.TryAdd(@event.TaskId, @event.Thread))
        {
            return null;
        }

        return Transition(entityId, indicators, nameof(TaskCreated), @event.OccurredAt, @event.Thread, previous);
    }

    private PositionLiveStateTransition? CompleteTask(PositionEntityId entityId, TaskCompleted @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.OpenTasks.Remove(@event.TaskId, out var threadId))
        {
            return null;
        }

        return Transition(entityId, indicators, nameof(TaskCompleted), @event.OccurredAt, threadId, previous);
    }

    private PositionLiveStateTransition? StartProcessing(
        PositionEntityId entityId,
        MessageDispatched @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.ProcessingMessages.TryAdd(@event.Message, @event.Thread))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            nameof(MessageDispatched),
            @event.OccurredAt,
            @event.Thread,
            previous);
    }

    private PositionLiveStateTransition? CompleteProcessing(
        PositionEntityId entityId,
        MessageProcessingCompleted @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.ProcessingMessages.Remove(@event.Message, out var threadId))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            nameof(MessageProcessingCompleted),
            @event.OccurredAt,
            threadId,
            previous);
    }

    private PositionLiveStateTransition? RetainAction(PositionEntityId entityId, ActionRetained @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.RetainedActions.TryAdd(@event.Action.Id, @event.Action.ThreadId))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            nameof(ActionRetained),
            @event.OccurredAt,
            @event.Action.ThreadId,
            previous);
    }

    private PositionLiveStateTransition? ObserveAuthorizedAction(
        PositionEntityId entityId,
        RetainedActionAuthorized @event)
    {
        var indicators = Indicators(entityId);
        if (!indicators.RetainedActions.ContainsKey(@event.Grant.RetainedActionId))
        {
            return null;
        }

        var previous = indicators.Evaluate();
        return Transition(
            entityId,
            indicators,
            nameof(RetainedActionAuthorized),
            @event.OccurredAt,
            @event.Grant.Thread,
            previous);
    }

    private PositionLiveStateTransition? CloseRetainedAction(
        PositionEntityId entityId,
        RetainedActionId actionId,
        PositionEvent @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.RetainedActions.Remove(actionId, out var threadId))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            @event.GetType().Name,
            @event.OccurredAt,
            threadId,
            previous);
    }

    private PositionLiveStateTransition? ObserveReturnedAction(
        PositionEntityId entityId,
        RetainedActionReturned @event)
    {
        var indicators = Indicators(entityId);
        if (!indicators.RetainedActions.TryGetValue(@event.ActionId, out var threadId))
        {
            return null;
        }

        var previous = indicators.Evaluate();
        return Transition(
            entityId,
            indicators,
            nameof(RetainedActionReturned),
            @event.OccurredAt,
            threadId,
            previous);
    }

    private PositionLiveStateTransition? ApplyConfigurationReady(
        PositionEntityId entityId,
        PositionConfigurationApplied @event)
    {
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!Set(ref indicators.ConfigurationBlocked, value: false))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            nameof(PositionConfigurationApplied),
            @event.OccurredAt,
            threadId: null,
            previous);
    }

    private PositionLiveStateTransition? OpenEscalation(Escalation message)
    {
        if (message.From is not PositionEndpointRef source)
        {
            return null;
        }

        var entityId = PositionEntityId.From(message.OrganizationId, source.PositionId);
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.UnresolvedEscalations.Add(message.Thread))
        {
            return null;
        }

        return Transition(entityId, indicators, nameof(Escalation), message.SentAt, message.Thread, previous);
    }

    private PositionLiveStateTransition? ResolveEscalation(OrgMessage message)
    {
        if (message.To is not PositionEndpointRef destination)
        {
            return null;
        }

        var entityId = PositionEntityId.From(message.OrganizationId, destination.PositionId);
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.UnresolvedEscalations.Remove(message.Thread))
        {
            return null;
        }

        return Transition(entityId, indicators, message.GetType().Name, message.SentAt, message.Thread, previous);
    }

    private PositionLiveStateTransition? OpenApproval(ApprovalRequest message)
    {
        if (message.From is not PositionEndpointRef source)
        {
            return null;
        }

        var entityId = PositionEntityId.From(message.OrganizationId, source.PositionId);
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.PendingApprovals.TryAdd(message.Id, message.Thread))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            nameof(ApprovalRequest),
            message.SentAt,
            message.Thread,
            previous);
    }

    private PositionLiveStateTransition? ResolveApproval(ApprovalDecision message)
    {
        if (message.To is not PositionEndpointRef destination)
        {
            return null;
        }

        var entityId = PositionEntityId.From(message.OrganizationId, destination.PositionId);
        var indicators = Indicators(entityId);
        var previous = indicators.Evaluate();
        if (!indicators.PendingApprovals.Remove(message.RequestId, out var threadId))
        {
            return null;
        }

        return Transition(
            entityId,
            indicators,
            nameof(ApprovalDecision),
            message.SentAt,
            threadId,
            previous);
    }

    private PositionIndicators Indicators(PositionEntityId entityId)
    {
        if (!_positions.TryGetValue(entityId.Value, out var indicators))
        {
            indicators = new PositionIndicators();
            _positions.Add(entityId.Value, indicators);
        }

        return indicators;
    }

    private static PositionEntityId FactEntity(PositionLiveStateProjectionFact fact) =>
        PositionEntityId.From(
            fact.OrganizationId,
            fact.PositionId
            ?? throw new InvalidOperationException(
                $"Projection fact '{fact.FactType}' has no position identifier."));

    private static PositionLiveStateTransition Transition(
        PositionEntityId entityId,
        PositionIndicators indicators,
        string eventType,
        DateTimeOffset occurredAt,
        ThreadId? threadId,
        PositionLiveState previousState) =>
        new(
            entityId,
            previousState,
            indicators.Evaluate(),
            eventType,
            occurredAt.ToUniversalTime(),
            threadId is null
                ? null
                : new PositionLiveStateCorrelatedEvent(
                    eventType,
                    threadId.Value,
                    occurredAt.ToUniversalTime()));

    private static bool Set(ref bool target, bool value)
    {
        if (target == value)
        {
            return false;
        }

        target = value;
        return true;
    }

    private sealed class PositionIndicators
    {
        public bool OutsideWorkingHours;
        public bool KillSwitchActive;
        public bool ConfigurationBlocked;
        public Dictionary<PositionTaskId, ThreadId> OpenTasks { get; } = [];
        public Dictionary<MessageId, ThreadId> ProcessingMessages { get; } = [];
        public HashSet<ThreadId> UnresolvedEscalations { get; } = [];
        public Dictionary<MessageId, ThreadId> PendingApprovals { get; } = [];
        public Dictionary<RetainedActionId, ThreadId> RetainedActions { get; } = [];

        public PositionLiveState Evaluate()
        {
            if (OutsideWorkingHours || KillSwitchActive)
            {
                return PositionLiveState.Offline;
            }

            if (ConfigurationBlocked || UnresolvedEscalations.Count != 0)
            {
                return PositionLiveState.Blocked;
            }

            if (PendingApprovals.Count != 0 || RetainedActions.Count != 0)
            {
                return PositionLiveState.WaitingHuman;
            }

            return OpenTasks.Count != 0 || ProcessingMessages.Count != 0
                ? PositionLiveState.Working
                : PositionLiveState.Idle;
        }
    }
}

internal enum PositionLiveStateCondition
{
    OutsideWorkingHours,
    KillSwitch,
    ConfigurationBlocked,
}

internal sealed record PositionLiveStateConditionFact
{
    public PositionLiveStateConditionFact(
        PositionEntityId entityId,
        PositionLiveStateCondition condition,
        bool isActive,
        DateTimeOffset occurredAtUtc)
    {
        if (!Enum.IsDefined(condition))
        {
            throw new ArgumentOutOfRangeException(nameof(condition), condition, "Unknown live-state condition.");
        }

        if (occurredAtUtc == default || occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Live-state condition timestamp must be specified with the UTC offset.",
                nameof(occurredAtUtc));
        }

        EntityId = entityId ?? throw new ArgumentNullException(nameof(entityId));
        Condition = condition;
        IsActive = isActive;
        OccurredAtUtc = occurredAtUtc;
    }

    public PositionEntityId EntityId { get; }

    public PositionLiveStateCondition Condition { get; }

    public bool IsActive { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string EventType => Condition switch
    {
        PositionLiveStateCondition.OutsideWorkingHours =>
            IsActive ? "WorkingHoursExited" : "WorkingHoursEntered",
        PositionLiveStateCondition.KillSwitch =>
            IsActive ? "KillSwitchActivated" : "KillSwitchDeactivated",
        PositionLiveStateCondition.ConfigurationBlocked =>
            IsActive ? "ConfigurationBlocked" : "ConfigurationReady",
        _ => throw new ArgumentOutOfRangeException(nameof(Condition), Condition, "Unknown live-state condition."),
    };
}

internal sealed record PositionLiveStateTransition(
    PositionEntityId EntityId,
    PositionLiveState PreviousState,
    PositionLiveState State,
    string EventType,
    DateTimeOffset OccurredAtUtc,
    PositionLiveStateCorrelatedEvent? CorrelatedEvent)
{
    public bool StateChanged => State != PreviousState;
}
