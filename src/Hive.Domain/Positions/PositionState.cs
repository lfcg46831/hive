using System.Collections.Immutable;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// The recoverable live state of a <c>PositionActor</c> (US-F0-06-T06a), reconstructed from
/// persisted events and snapshots before the entity accepts new commands. Directive checkpoint
/// revisions extend that state additively for US-F0-19-T03.
/// </summary>
public sealed record PositionState
{
    private PositionState(
        ImmutableArray<OrgMessage> inbox,
        ImmutableDictionary<PositionTaskId, PersistedTask> openTasks,
        ImmutableDictionary<string, string> shortMemory,
        ImmutableDictionary<string, ShortMemoryContextScope> shortMemoryContextScopes,
        ImmutableArray<MessageId> recentHistory,
        ImmutableArray<OrgMessage> materializedHistory,
        ImmutableHashSet<MessageId> processedMessages,
        OccupantId? occupant,
        OccupantType? occupantType,
        PositionConfigurationStamp? lastConfigurationStamp,
        ImmutableDictionary<RetainedActionId, PersistedRetainedAction> retainedActions,
        ImmutableDictionary<DirectiveId, DirectiveCheckpoint> directiveCheckpoints,
        ImmutableArray<OccupantReplyEmitted> occupantReplies)
    {
        Inbox = inbox;
        OpenTasks = openTasks;
        ShortMemory = shortMemory;
        ShortMemoryContextScopes = shortMemoryContextScopes;
        RecentHistory = recentHistory;
        MaterializedHistory = materializedHistory;
        ProcessedMessages = processedMessages;
        Occupant = occupant;
        OccupantType = occupantType;
        LastConfigurationStamp = lastConfigurationStamp;
        RetainedActions = retainedActions;
        DirectiveCheckpoints = directiveCheckpoints;
        OccupantReplies = occupantReplies;
    }

    /// <summary>The initial state before any snapshot or event has been replayed.</summary>
    public static PositionState Empty { get; } = new(
        ImmutableArray<OrgMessage>.Empty,
        ImmutableDictionary<PositionTaskId, PersistedTask>.Empty,
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal),
        ImmutableDictionary.Create<string, ShortMemoryContextScope>(StringComparer.Ordinal),
        ImmutableArray<MessageId>.Empty,
        ImmutableArray<OrgMessage>.Empty,
        ImmutableHashSet<MessageId>.Empty,
        occupant: null,
        occupantType: null,
        lastConfigurationStamp: null,
        ImmutableDictionary<RetainedActionId, PersistedRetainedAction>.Empty,
        ImmutableDictionary<DirectiveId, DirectiveCheckpoint>.Empty,
        ImmutableArray<OccupantReplyEmitted>.Empty);

    /// <summary>The messages admitted but not yet dispatched.</summary>
    public ImmutableArray<OrgMessage> Inbox { get; }

    /// <summary>The tasks currently in progress, keyed by task identity.</summary>
    public ImmutableDictionary<PositionTaskId, PersistedTask> OpenTasks { get; }

    /// <summary>The position's short-term memory entries.</summary>
    public ImmutableDictionary<string, string> ShortMemory { get; }

    /// <summary>The optional AI-context scope attached to each explicitly eligible memory key.</summary>
    public ImmutableDictionary<string, ShortMemoryContextScope> ShortMemoryContextScopes { get; }

    /// <summary>The recently dispatched message ids, in replay order.</summary>
    public ImmutableArray<MessageId> RecentHistory { get; }

    /// <summary>The recently dispatched messages retained for correlated prompt context.</summary>
    public ImmutableArray<OrgMessage> MaterializedHistory { get; }

    /// <summary>The message ids already accepted by the position.</summary>
    public ImmutableHashSet<MessageId> ProcessedMessages { get; }

    /// <summary>The current occupant, or null when the position has none yet.</summary>
    public OccupantId? Occupant { get; }

    /// <summary>The current occupant type, or null when the position has none yet.</summary>
    public OccupantType? OccupantType { get; }

    /// <summary>The latest runtime configuration stamp accepted by the position entity.</summary>
    public PositionConfigurationStamp? LastConfigurationStamp { get; }

    /// <summary>Actions retained by the authority gate, keyed by durable identity.</summary>
    public ImmutableDictionary<RetainedActionId, PersistedRetainedAction> RetainedActions { get; }

    /// <summary>The latest durable checkpoint revision for each directive handled here.</summary>
    public ImmutableDictionary<DirectiveId, DirectiveCheckpoint> DirectiveCheckpoints { get; }

    /// <summary>Canonical occupant-authored replies emitted by this position.</summary>
    public ImmutableArray<OccupantReplyEmitted> OccupantReplies { get; }

    /// <summary>Rebuilds live state from a persisted point-in-time snapshot.</summary>
    public static PositionState Restore(PositionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new PositionState(
            snapshot.Inbox,
            snapshot.OpenTasks.ToImmutableDictionary(task => task.TaskId),
            snapshot.ShortMemory,
            snapshot.ShortMemoryContextScopes,
            snapshot.RecentHistory,
            snapshot.MaterializedHistory,
            snapshot.ProcessedMessages.ToImmutableHashSet(),
            snapshot.Occupant,
            snapshot.OccupantType,
            snapshot.LastConfigurationStamp,
            snapshot.RetainedActions.ToImmutableDictionary(action => action.Id),
            snapshot.DirectiveCheckpoints.ToImmutableDictionary(
                checkpoint => checkpoint.Correlation.DirectiveId),
            snapshot.OccupantReplies);
    }

    /// <summary>Exports the live state into the persisted snapshot shape.</summary>
    public PositionSnapshot ToSnapshot(DateTimeOffset takenAt) => new(
        takenAt,
        Occupant,
        OccupantType,
        Inbox,
        OpenTasks.Values.OrderBy(task => task.TaskId.Value),
        ShortMemory,
        RecentHistory,
        ProcessedMessages.OrderBy(message => message.Value),
        LastConfigurationStamp,
        RetainedActions.Values.OrderBy(action => action.Id.Value),
        ShortMemoryContextScopes,
        MaterializedHistory,
        DirectiveCheckpoints.Values.OrderBy(
            checkpoint => checkpoint.Correlation.DirectiveId.Value),
        OccupantReplies.OrderBy(reply => reply.Message.Id.Value));

    /// <summary>
    /// Evaluates an attempted checkpoint revision without mutating state. Re-delivered or stale
    /// revisions are idempotent no-ops; new revisions must be contiguous, monotonic and retain the
    /// original plan, correlation and completed-subtask evidence.
    /// </summary>
    public DirectiveCheckpointPersistenceDecision EvaluateDirectiveCheckpointPersistence(
        PositionEntityId entityId,
        DirectiveCheckpoint checkpoint)
    {
        ArgumentNullException.ThrowIfNull(entityId);
        ArgumentNullException.ThrowIfNull(checkpoint);

        if (!IsStructurallyPersistable(entityId, checkpoint))
        {
            return DirectiveCheckpointPersistenceDecision.Rejected;
        }

        if (!DirectiveCheckpoints.TryGetValue(
                checkpoint.Correlation.DirectiveId,
                out var existing))
        {
            return checkpoint.Revision == 1
                ? DirectiveCheckpointPersistenceDecision.Persist
                : DirectiveCheckpointPersistenceDecision.Rejected;
        }

        if (checkpoint.ContractVersion != existing.ContractVersion ||
            checkpoint.Correlation != existing.Correlation ||
            !PlansEqual(checkpoint.Plan, existing.Plan))
        {
            return DirectiveCheckpointPersistenceDecision.Rejected;
        }

        if (checkpoint.Revision < existing.Revision)
        {
            return RetainsCompletedSubtasks(checkpoint, existing)
                ? DirectiveCheckpointPersistenceDecision.AlreadyPersisted
                : DirectiveCheckpointPersistenceDecision.Rejected;
        }

        if (checkpoint.Revision == existing.Revision)
        {
            return CheckpointRevisionEqual(checkpoint, existing)
                ? DirectiveCheckpointPersistenceDecision.AlreadyPersisted
                : DirectiveCheckpointPersistenceDecision.Rejected;
        }

        if (checkpoint.Revision != existing.Revision + 1 ||
            !RetainsCompletedSubtasks(existing, checkpoint) ||
            !ContainsCheckpointTransition(existing, checkpoint))
        {
            return DirectiveCheckpointPersistenceDecision.Rejected;
        }

        return DirectiveCheckpointPersistenceDecision.Persist;
    }

    /// <summary>Evaluates whether the recovered state is currently safe to passivate.</summary>
    public PositionPassivationDecision EvaluatePassivation(PositionRuntimeConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var reasons = ImmutableArray.CreateBuilder<PositionPassivationBlockReason>();
        if (!Inbox.IsEmpty)
        {
            reasons.Add(PositionPassivationBlockReason.PendingDelivery);
        }

        if (OpenTasks.Values.Any(task => task.Priority == Priority.Critical))
        {
            reasons.Add(PositionPassivationBlockReason.CriticalTaskOpen);
        }

        if (!configuration.Schedules.IsEmpty)
        {
            reasons.Add(PositionPassivationBlockReason.ActiveSchedule);
        }

        if (!configuration.Occupant.Subscriptions.IsEmpty)
        {
            reasons.Add(PositionPassivationBlockReason.ActiveSubscription);
        }

        if (!RetainedActions.IsEmpty)
        {
            reasons.Add(PositionPassivationBlockReason.RetainedAction);
        }

        return new PositionPassivationDecision(reasons);
    }

    /// <summary>Applies one persisted event to the recoverable state.</summary>
    public PositionState Apply(PositionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        return @event switch
        {
            MessageReceived received => Apply(received),
            TaskCreated created => Apply(created),
            TaskUpdated updated => Apply(updated),
            TaskCompleted completed => Apply(completed),
            ShortMemoryUpdated updated => Apply(updated),
            OccupantChanged changed => Apply(changed),
            MessageDispatched dispatched => Apply(dispatched),
            MessageProcessingCompleted completed => Apply(completed),
            PositionPassivated => this,
            PositionConfigurationApplied applied => Apply(applied),
            ActionRetained retained => Apply(retained),
            RetainedActionAuthorized authorized => Apply(authorized),
            RetainedActionDenied denied => Apply(denied),
            RetainedActionConsumed consumed => Apply(consumed),
            RetainedActionExpired expired => Apply(expired),
            RetainedActionReturned returned => Apply(returned),
            DirectiveCheckpointPersisted persisted => Apply(persisted),
            OccupantReplyEmitted emitted => Apply(emitted),
            _ => this,
        };
    }

    private PositionState Apply(MessageReceived @event) => new(
        Inbox.Add(@event.Message),
        OpenTasks,
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages.Add(@event.Message.Id),
        Occupant,
        OccupantType,
        LastConfigurationStamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(TaskCreated @event) => new(
        Inbox,
        OpenTasks.SetItem(
            @event.TaskId,
            new PersistedTask(
                @event.TaskId,
                @event.Thread,
                @event.Title,
                @event.Priority,
                @event.OccurredAt,
                @event.Deadline,
                @event.CausedBy)),
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(TaskUpdated @event)
    {
        if (!OpenTasks.TryGetValue(@event.TaskId, out var existing))
        {
            return this;
        }

        var updated = new PersistedTask(
            existing.TaskId,
            existing.Thread,
            existing.Title,
            @event.Priority ?? existing.Priority,
            existing.OpenedAt,
            @event.Deadline ?? existing.Deadline,
            existing.CausedBy,
            @event.Note);

        return new PositionState(
            Inbox,
            OpenTasks.SetItem(@event.TaskId, updated),
            ShortMemory,
            ShortMemoryContextScopes,
            RecentHistory,
            MaterializedHistory,
            ProcessedMessages,
            Occupant,
            OccupantType,
            LastConfigurationStamp,
            RetainedActions,
            DirectiveCheckpoints,
            OccupantReplies);
    }

    private PositionState Apply(TaskCompleted @event) => new(
        Inbox,
        OpenTasks.Remove(@event.TaskId),
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(ShortMemoryUpdated @event) => new(
        Inbox,
        OpenTasks,
        @event.Value.Length == 0
            ? ShortMemory.Remove(@event.Key)
            : ShortMemory.SetItem(@event.Key, @event.Value),
        @event.Value.Length == 0 || @event.ContextScope is null
            ? ShortMemoryContextScopes.Remove(@event.Key)
            : ShortMemoryContextScopes.SetItem(@event.Key, @event.ContextScope),
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(OccupantChanged @event) => new(
        Inbox,
        OpenTasks,
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        @event.Occupant,
        @event.Type,
        LastConfigurationStamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(MessageDispatched @event)
    {
        var message = Inbox.FirstOrDefault(candidate => candidate.Id == @event.Message);
        var materializedHistory = message is null ||
            MaterializedHistory.Any(candidate => candidate.Id == message.Id)
                ? MaterializedHistory
                : MaterializedHistory.Add(message);

        return new PositionState(
            Inbox,
            OpenTasks,
            ShortMemory,
            ShortMemoryContextScopes,
            RecentHistory.Contains(@event.Message)
                ? RecentHistory
                : RecentHistory.Add(@event.Message),
            materializedHistory,
            ProcessedMessages,
            Occupant,
            OccupantType,
            LastConfigurationStamp,
            RetainedActions,
            DirectiveCheckpoints,
            OccupantReplies);
    }

    private PositionState Apply(MessageProcessingCompleted @event) => new(
        Inbox.RemoveAll(message => message.Id == @event.Message),
        OpenTasks,
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(PositionConfigurationApplied @event) => new(
        Inbox,
        OpenTasks,
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        @event.Stamp,
        RetainedActions,
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(ActionRetained @event)
    {
        if (RetainedActions.ContainsKey(@event.Action.Id)
            || RetainedActions.Values.Any(action => string.Equals(
                action.CorrelationId,
                @event.Action.CorrelationId,
                StringComparison.Ordinal)))
        {
            return this;
        }

        return new PositionState(
            Inbox,
            OpenTasks,
            ShortMemory,
            ShortMemoryContextScopes,
            RecentHistory,
            MaterializedHistory,
            ProcessedMessages,
            Occupant,
            OccupantType,
            LastConfigurationStamp,
            RetainedActions.Add(@event.Action.Id, @event.Action),
            DirectiveCheckpoints,
            OccupantReplies);
    }

    private PositionState Apply(RetainedActionAuthorized @event)
    {
        if (!RetainedActions.TryGetValue(@event.Grant.RetainedActionId, out var action)
            || action.State != RetainedActionState.Retained
            || action.AuthorizationGrant?.Id == @event.Grant.Id
            || !TargetsAction(@event.Grant, action))
        {
            return this;
        }

        return WithAction(action.Authorize(@event.Grant, @event.OccurredAt));
    }

    private PositionState Apply(RetainedActionDenied @event)
    {
        if (!RetainedActions.TryGetValue(@event.Denial.RetainedActionId, out var action)
            || action.State != RetainedActionState.Retained
            || !TargetsAction(@event.Denial, action))
        {
            return this;
        }

        return WithAction(action.Deny(@event.Denial, @event.OccurredAt));
    }

    private PositionState Apply(RetainedActionConsumed @event)
    {
        if (!TryGetAuthorized(@event.ActionId, @event.GrantId, out var action))
        {
            return this;
        }

        return WithAction(action.Consume(@event.OccurredAt));
    }

    private PositionState Apply(RetainedActionExpired @event)
    {
        if (!TryGetAuthorized(@event.ActionId, @event.GrantId, out var action))
        {
            return this;
        }

        return WithAction(action.Expire(@event.OccurredAt, @event.ReEscalationCode));
    }

    private PositionState Apply(RetainedActionReturned @event)
    {
        if (!TryGetAuthorized(@event.ActionId, @event.GrantId, out var action))
        {
            return this;
        }

        return WithAction(action.ReturnToRetained(@event.OccurredAt, @event.ReEscalationCode));
    }

    private bool TryGetAuthorized(
        RetainedActionId actionId,
        MessageId grantId,
        out PersistedRetainedAction action)
    {
        if (RetainedActions.TryGetValue(actionId, out var found)
            && found.State == RetainedActionState.Authorized
            && found.ActiveGrant?.Id == grantId)
        {
            action = found;
            return true;
        }

        action = null!;
        return false;
    }

    private static bool TargetsAction(OrgMessage resolution, PersistedRetainedAction action) =>
        resolution.OrganizationId == action.OrganizationId
        && resolution.Thread == action.ThreadId
        && resolution.To is PositionEndpointRef destination
        && destination.PositionId == action.PositionId;

    private PositionState WithAction(PersistedRetainedAction action) => new(
        Inbox,
        OpenTasks,
        ShortMemory,
        ShortMemoryContextScopes,
        RecentHistory,
        MaterializedHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp,
        RetainedActions.SetItem(action.Id, action),
        DirectiveCheckpoints,
        OccupantReplies);

    private PositionState Apply(DirectiveCheckpointPersisted @event)
    {
        var checkpoint = @event.Checkpoint;
        if (DirectiveCheckpoints.TryGetValue(
                checkpoint.Correlation.DirectiveId,
                out var existing) &&
            existing.Revision >= checkpoint.Revision)
        {
            return this;
        }

        return new PositionState(
            Inbox,
            OpenTasks,
            ShortMemory,
            ShortMemoryContextScopes,
            RecentHistory,
            MaterializedHistory,
            ProcessedMessages,
            Occupant,
            OccupantType,
            LastConfigurationStamp,
            RetainedActions,
            DirectiveCheckpoints.SetItem(
                checkpoint.Correlation.DirectiveId,
                checkpoint),
            OccupantReplies);
    }

    private PositionState Apply(OccupantReplyEmitted @event)
    {
        var existing = OccupantReplies.FirstOrDefault(
            reply => reply.Message.Id == @event.Message.Id);
        if (existing is not null)
        {
            return existing == @event
                ? this
                : throw new InvalidOperationException(
                    $"Occupant reply message '{@event.Message.Id}' was replayed with conflicting content.");
        }

        return new PositionState(
            Inbox,
            OpenTasks,
            ShortMemory,
            ShortMemoryContextScopes,
            RecentHistory,
            MaterializedHistory,
            ProcessedMessages,
            Occupant,
            OccupantType,
            LastConfigurationStamp,
            RetainedActions,
            DirectiveCheckpoints,
            OccupantReplies.Add(@event));
    }

    private static bool IsStructurallyPersistable(
        PositionEntityId entityId,
        DirectiveCheckpoint checkpoint)
    {
        if (checkpoint.ContractVersion != DirectiveCheckpointContractVersions.V1 ||
            checkpoint.Plan.ContractVersion != DirectiveCheckpointContractVersions.V1 ||
            checkpoint.Correlation.OrganizationId != entityId.Organization ||
            checkpoint.Correlation.PositionId != entityId.Position ||
            !DirectiveCheckpointContextProjector.TryProject(checkpoint, out _))
        {
            return false;
        }

        var planIds = checkpoint.Plan.Subtasks
            .Select(subtask => subtask.LocalId)
            .ToHashSet(StringComparer.Ordinal);
        if (checkpoint.CompletedSubtasks.Any(completed => !planIds.Contains(completed.LocalId)) ||
            checkpoint.NextSubtaskId is { } next &&
            (!planIds.Contains(next) || checkpoint.CompletedSubtasks.Any(completed =>
                string.Equals(completed.LocalId, next, StringComparison.Ordinal))))
        {
            return false;
        }

        return true;
    }

    private static bool PlansEqual(
        DirectiveCheckpointPlan left,
        DirectiveCheckpointPlan right) =>
        left.ContractVersion == right.ContractVersion &&
        left.Subtasks.Length == right.Subtasks.Length &&
        left.Subtasks.Zip(right.Subtasks).All(pair =>
            pair.First.Sequence == pair.Second.Sequence &&
            string.Equals(pair.First.LocalId, pair.Second.LocalId, StringComparison.Ordinal) &&
            string.Equals(pair.First.Objective, pair.Second.Objective, StringComparison.Ordinal) &&
            pair.First.EstimatedDuration == pair.Second.EstimatedDuration &&
            pair.First.CompletionCriteria.SequenceEqual(
                pair.Second.CompletionCriteria,
                StringComparer.Ordinal));

    private static bool RetainsCompletedSubtasks(
        DirectiveCheckpoint existing,
        DirectiveCheckpoint candidate)
    {
        var candidateById = candidate.CompletedSubtasks.ToDictionary(
            completed => completed.LocalId,
            StringComparer.Ordinal);
        return existing.CompletedSubtasks.All(completed =>
            candidateById.TryGetValue(completed.LocalId, out var retained) &&
            completed.EvidenceReferences.SequenceEqual(retained.EvidenceReferences));
    }

    private static bool ContainsCheckpointTransition(
        DirectiveCheckpoint existing,
        DirectiveCheckpoint candidate) =>
        candidate.CompletedSubtasks.Length != existing.CompletedSubtasks.Length ||
        !candidate.Blockers.SequenceEqual(existing.Blockers) ||
        !string.Equals(
            candidate.NextSubtaskId,
            existing.NextSubtaskId,
            StringComparison.Ordinal);

    private static bool CheckpointRevisionEqual(
        DirectiveCheckpoint left,
        DirectiveCheckpoint right) =>
        left.CompletedSubtasks.Length == right.CompletedSubtasks.Length &&
        RetainsCompletedSubtasks(left, right) &&
        RetainsCompletedSubtasks(right, left) &&
        left.Blockers.SequenceEqual(right.Blockers) &&
        string.Equals(left.NextSubtaskId, right.NextSubtaskId, StringComparison.Ordinal);
}
