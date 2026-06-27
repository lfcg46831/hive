using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// The recoverable live state of a <c>PositionActor</c> (US-F0-06-T06a), reconstructed from
/// persisted events and snapshots before the entity accepts new commands.
/// </summary>
public sealed record PositionState
{
    private PositionState(
        ImmutableArray<OrgMessage> inbox,
        ImmutableDictionary<PositionTaskId, PersistedTask> openTasks,
        ImmutableDictionary<string, string> shortMemory,
        ImmutableArray<MessageId> recentHistory,
        ImmutableHashSet<MessageId> processedMessages,
        OccupantId? occupant,
        OccupantType? occupantType,
        PositionConfigurationStamp? lastConfigurationStamp)
    {
        Inbox = inbox;
        OpenTasks = openTasks;
        ShortMemory = shortMemory;
        RecentHistory = recentHistory;
        ProcessedMessages = processedMessages;
        Occupant = occupant;
        OccupantType = occupantType;
        LastConfigurationStamp = lastConfigurationStamp;
    }

    /// <summary>The initial state before any snapshot or event has been replayed.</summary>
    public static PositionState Empty { get; } = new(
        ImmutableArray<OrgMessage>.Empty,
        ImmutableDictionary<PositionTaskId, PersistedTask>.Empty,
        ImmutableDictionary.Create<string, string>(StringComparer.Ordinal),
        ImmutableArray<MessageId>.Empty,
        ImmutableHashSet<MessageId>.Empty,
        occupant: null,
        occupantType: null,
        lastConfigurationStamp: null);

    /// <summary>The messages admitted but not yet dispatched.</summary>
    public ImmutableArray<OrgMessage> Inbox { get; }

    /// <summary>The tasks currently in progress, keyed by task identity.</summary>
    public ImmutableDictionary<PositionTaskId, PersistedTask> OpenTasks { get; }

    /// <summary>The position's short-term memory entries.</summary>
    public ImmutableDictionary<string, string> ShortMemory { get; }

    /// <summary>The recently dispatched message ids, in replay order.</summary>
    public ImmutableArray<MessageId> RecentHistory { get; }

    /// <summary>The message ids already accepted by the position.</summary>
    public ImmutableHashSet<MessageId> ProcessedMessages { get; }

    /// <summary>The current occupant, or null when the position has none yet.</summary>
    public OccupantId? Occupant { get; }

    /// <summary>The current occupant type, or null when the position has none yet.</summary>
    public OccupantType? OccupantType { get; }

    /// <summary>The latest runtime configuration stamp accepted by the position entity.</summary>
    public PositionConfigurationStamp? LastConfigurationStamp { get; }

    /// <summary>Rebuilds live state from a persisted point-in-time snapshot.</summary>
    public static PositionState Restore(PositionSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        return new PositionState(
            snapshot.Inbox,
            snapshot.OpenTasks.ToImmutableDictionary(task => task.TaskId),
            snapshot.ShortMemory,
            snapshot.RecentHistory,
            snapshot.ProcessedMessages.ToImmutableHashSet(),
            snapshot.Occupant,
            snapshot.OccupantType,
            snapshot.LastConfigurationStamp);
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
        LastConfigurationStamp);

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
            PositionPassivated => this,
            PositionConfigurationApplied applied => Apply(applied),
            _ => this,
        };
    }

    private PositionState Apply(MessageReceived @event) => new(
        Inbox.Add(@event.Message),
        OpenTasks,
        ShortMemory,
        RecentHistory,
        ProcessedMessages.Add(@event.Message.Id),
        Occupant,
        OccupantType,
        LastConfigurationStamp);

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
        RecentHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp);

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
            existing.CausedBy);

        return new PositionState(
            Inbox,
            OpenTasks.SetItem(@event.TaskId, updated),
            ShortMemory,
            RecentHistory,
            ProcessedMessages,
            Occupant,
            OccupantType,
            LastConfigurationStamp);
    }

    private PositionState Apply(TaskCompleted @event) => new(
        Inbox,
        OpenTasks.Remove(@event.TaskId),
        ShortMemory,
        RecentHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp);

    private PositionState Apply(ShortMemoryUpdated @event) => new(
        Inbox,
        OpenTasks,
        @event.Value.Length == 0
            ? ShortMemory.Remove(@event.Key)
            : ShortMemory.SetItem(@event.Key, @event.Value),
        RecentHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp);

    private PositionState Apply(OccupantChanged @event) => new(
        Inbox,
        OpenTasks,
        ShortMemory,
        RecentHistory,
        ProcessedMessages,
        @event.Occupant,
        @event.Type,
        LastConfigurationStamp);

    private PositionState Apply(MessageDispatched @event) => new(
        Inbox.RemoveAll(message => message.Id == @event.Message),
        OpenTasks,
        ShortMemory,
        RecentHistory.Add(@event.Message),
        ProcessedMessages,
        Occupant,
        OccupantType,
        LastConfigurationStamp);

    private PositionState Apply(PositionConfigurationApplied @event) => new(
        Inbox,
        OpenTasks,
        ShortMemory,
        RecentHistory,
        ProcessedMessages,
        Occupant,
        OccupantType,
        @event.Stamp);
}
