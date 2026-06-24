using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Record progress on an open task and/or revise its attributes. The <see cref="Note"/> is the
/// required progress text; <see cref="Priority"/> and <see cref="Deadline"/> are optional revisions
/// that, when present, replace the current value. How a present-but-null revision is reconciled
/// against the existing task state belongs to the state reducer (US-F0-06-T06), not to this contract.
/// </summary>
public sealed record UpdateTask : PositionCommand
{
    public UpdateTask(
        PositionTaskId taskId,
        string note,
        Priority? priority = null,
        DateTimeOffset? deadline = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        TaskId = taskId;
        Note = CommandText.RequireContent(note, nameof(note));
        Priority = priority is { } value
            ? PriorityContract.RequireDefined(value, nameof(priority))
            : null;
        Deadline = deadline;
    }

    /// <summary>The identity of the open task to update.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>A short human-readable progress note.</summary>
    public string Note { get; }

    /// <summary>The revised priority, when the update changes it.</summary>
    public Priority? Priority { get; }

    /// <summary>The revised deadline, when the update changes it.</summary>
    public DateTimeOffset? Deadline { get; }
}
