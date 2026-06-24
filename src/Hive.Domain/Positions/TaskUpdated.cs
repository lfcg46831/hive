using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Progress was recorded on an open task and/or its attributes were revised — the fact produced by a
/// successful <see cref="UpdateTask"/>. <see cref="Note"/> is the progress text; a present
/// <see cref="Priority"/> or <see cref="Deadline"/> is the revised value. How a present-but-null
/// deadline reconciles against the prior task state is the reducer's concern (US-F0-06-T06a), not
/// this contract's.
/// </summary>
public sealed record TaskUpdated : PositionEvent
{
    public TaskUpdated(
        PositionTaskId taskId,
        string note,
        DateTimeOffset occurredAt,
        Priority? priority = null,
        DateTimeOffset? deadline = null)
        : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        TaskId = taskId;
        Note = CommandText.RequireContent(note, nameof(note));
        Priority = priority is { } value
            ? PriorityContract.RequireDefined(value, nameof(priority))
            : null;
        Deadline = deadline;
    }

    /// <summary>The identity of the open task that was updated.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>A short human-readable progress note.</summary>
    public string Note { get; }

    /// <summary>The revised priority, when the update changed it.</summary>
    public Priority? Priority { get; }

    /// <summary>The revised deadline, when the update changed it.</summary>
    public DateTimeOffset? Deadline { get; }
}
