using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Base contract for PositionActor audit/read-model projection signals (US-F0-06-T10).
/// </summary>
public abstract record PositionProjectionEvent
{
    private protected PositionProjectionEvent(PositionEntityId entityId, DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(entityId);

        EntityId = entityId;
        OccurredAt = occurredAt;
    }

    /// <summary>The position entity that produced the projection signal.</summary>
    public PositionEntityId EntityId { get; }

    /// <summary>When the projected fact or lifecycle decision occurred.</summary>
    public DateTimeOffset OccurredAt { get; }
}

/// <summary>
/// A persisted position event has been confirmed by the journal and can feed audit/read models.
/// </summary>
public sealed record PositionEventCommitted : PositionProjectionEvent
{
    public PositionEventCommitted(PositionEntityId entityId, PositionEvent @event)
        : base(entityId, (@event ?? throw new ArgumentNullException(nameof(@event))).OccurredAt)
    {
        Event = @event;
    }

    /// <summary>The journal-confirmed position event.</summary>
    public PositionEvent Event { get; }
}

/// <summary>
/// A retained action is durable and its already-materialized governance messages may now be
/// handed to routing/dispatch. This signal is never published during recovery.
/// </summary>
public sealed record PositionRetainedActionReady : PositionProjectionEvent
{
    public PositionRetainedActionReady(PositionEntityId entityId, PersistedRetainedAction action)
        : base(entityId, (action ?? throw new ArgumentNullException(nameof(action))).RetainedAt)
    {
        Action = action;
    }

    public PersistedRetainedAction Action { get; }
}

/// <summary>The position restored its durable state from snapshot/journal replay.</summary>
public sealed record PositionRecovered : PositionProjectionEvent
{
    public PositionRecovered(
        PositionEntityId entityId,
        PositionConfigurationStamp? lastConfigurationStamp,
        DateTimeOffset occurredAt)
        : base(entityId, occurredAt)
    {
        LastConfigurationStamp = lastConfigurationStamp;
    }

    /// <summary>The latest configuration stamp recovered from durable state, when one exists.</summary>
    public PositionConfigurationStamp? LastConfigurationStamp { get; }
}

/// <summary>The position completed activation and is ready to process business commands again.</summary>
public sealed record PositionReactivated : PositionProjectionEvent
{
    public PositionReactivated(
        PositionEntityId entityId,
        PositionConfigurationStamp? lastConfigurationStamp,
        DateTimeOffset occurredAt)
        : base(entityId, occurredAt)
    {
        LastConfigurationStamp = lastConfigurationStamp;
    }

    /// <summary>The configuration stamp active when the entity became ready.</summary>
    public PositionConfigurationStamp? LastConfigurationStamp { get; }
}

/// <summary>A redelivered message was rejected because the recovered processed set already contains it.</summary>
public sealed record PositionMessageDuplicateRejected : PositionProjectionEvent
{
    public PositionMessageDuplicateRejected(
        PositionEntityId entityId,
        MessageId message,
        ThreadId thread,
        DateTimeOffset occurredAt)
        : base(entityId, occurredAt)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(thread);

        Message = message;
        Thread = thread;
    }

    /// <summary>The duplicate message id.</summary>
    public MessageId Message { get; }

    /// <summary>The thread carried by the duplicate message.</summary>
    public ThreadId Thread { get; }
}

/// <summary>A fail-closed runtime configuration decision blocked the position from processing work.</summary>
public sealed record PositionConfigurationRejected : PositionProjectionEvent
{
    public PositionConfigurationRejected(
        PositionEntityId entityId,
        PositionConfigurationBlockReason reason,
        PositionConfigurationStamp? recoveredStamp,
        PositionConfigurationStamp? loadedStamp,
        DateTimeOffset occurredAt)
        : base(entityId, occurredAt)
    {
        if (!Enum.IsDefined(reason))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                reason,
                "Unknown position configuration block reason.");
        }

        Reason = reason;
        RecoveredStamp = recoveredStamp;
        LoadedStamp = loadedStamp;
    }

    /// <summary>The coarse fail-closed reason produced by the compatibility matrix.</summary>
    public PositionConfigurationBlockReason Reason { get; }

    /// <summary>The stamp recovered from durable state, when one exists.</summary>
    public PositionConfigurationStamp? RecoveredStamp { get; }

    /// <summary>The incompatible loaded stamp, when the provider returned a loaded configuration.</summary>
    public PositionConfigurationStamp? LoadedStamp { get; }
}
