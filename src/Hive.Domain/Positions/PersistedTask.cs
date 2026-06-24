using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// The persisted shape of one open task inside a <see cref="PositionSnapshot"/>: the durable
/// attributes of a unit of work in progress, as they stand when the snapshot is taken. It captures
/// the facts an <see cref="OpenTask"/>/<see cref="TaskCreated"/> established plus the latest revised
/// <see cref="Priority"/>/<see cref="Deadline"/>, so a restore from snapshot reconstructs the open
/// set without replaying the events that produced it. Only open tasks live in a snapshot; a completed
/// task is dropped, not retained.
/// </summary>
public sealed record PersistedTask
{
    public PersistedTask(
        PositionTaskId taskId,
        ThreadId thread,
        string title,
        Priority priority,
        DateTimeOffset openedAt,
        DateTimeOffset? deadline = null,
        MessageId? causedBy = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(thread);

        TaskId = taskId;
        Thread = thread;
        Title = CommandText.RequireContent(title, nameof(title));
        Priority = PriorityContract.RequireDefined(priority, nameof(priority));
        OpenedAt = openedAt;
        Deadline = deadline;
        CausedBy = causedBy;
    }

    /// <summary>The identity of the open task.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>The conversation/thread the task belongs to.</summary>
    public ThreadId Thread { get; }

    /// <summary>A short human-readable description of the work.</summary>
    public string Title { get; }

    /// <summary>The current task priority.</summary>
    public Priority Priority { get; }

    /// <summary>When the task was opened.</summary>
    public DateTimeOffset OpenedAt { get; }

    /// <summary>The current deadline, when the task has one.</summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>The inbound message that triggered the task, when there is one.</summary>
    public MessageId? CausedBy { get; }
}
