using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// A new unit of work was opened on the position — the fact produced by a successful
/// <see cref="OpenTask"/>. It records the durable attributes of the task at creation: its
/// caller-supplied <see cref="TaskId"/>, the <see cref="Thread"/> it belongs to, the
/// <see cref="Title"/>, <see cref="Priority"/>, an optional <see cref="Deadline"/> and the inbound
/// message that caused it (<see cref="CausedBy"/>) when there was one.
/// </summary>
public sealed record TaskCreated : PositionEvent
{
    public TaskCreated(
        PositionTaskId taskId,
        ThreadId thread,
        string title,
        Priority priority,
        DateTimeOffset occurredAt,
        DateTimeOffset? deadline = null,
        MessageId? causedBy = null)
        : base(occurredAt)
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

    /// <summary>The identity of the task that was opened.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>The conversation/thread the task belongs to.</summary>
    public ThreadId Thread { get; }

    /// <summary>A short human-readable description of the work.</summary>
    public string Title { get; }

    /// <summary>The task priority at creation.</summary>
    public Priority Priority { get; }

    /// <summary>An optional deadline for the work.</summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>The inbound message that triggered the task, when there is one.</summary>
    public MessageId? CausedBy { get; }
}
