using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Open a new unit of work tracked by the position. The <see cref="TaskId"/> is supplied by the
/// caller so the open intent is itself idempotent, and the task is anchored to the
/// <see cref="Thread"/> it belongs to. <see cref="CausedBy"/> optionally records the inbound message
/// that triggered the work, linking the task back to the inbox.
/// </summary>
public sealed record OpenTask : PositionCommand
{
    public OpenTask(
        PositionTaskId taskId,
        ThreadId thread,
        string title,
        Priority priority,
        DateTimeOffset? deadline = null,
        MessageId? causedBy = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);
        ArgumentNullException.ThrowIfNull(thread);

        TaskId = taskId;
        Thread = thread;
        Title = CommandText.RequireContent(title, nameof(title));
        Priority = PriorityContract.RequireDefined(priority, nameof(priority));
        Deadline = deadline;
        CausedBy = causedBy;
    }

    /// <summary>The caller-supplied identity of the task to open.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>The conversation/thread the task belongs to.</summary>
    public ThreadId Thread { get; }

    /// <summary>A short human-readable description of the work.</summary>
    public string Title { get; }

    /// <summary>The task priority.</summary>
    public Priority Priority { get; }

    /// <summary>An optional deadline for the work.</summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>The inbound message that triggered the task, when there is one.</summary>
    public MessageId? CausedBy { get; }
}
