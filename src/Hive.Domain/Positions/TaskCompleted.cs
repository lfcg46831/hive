using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// An open task was concluded — the fact produced by a successful <see cref="CompleteTask"/>.
/// <see cref="Summary"/> optionally captures the outcome and, when present, carries content. Replay
/// uses this to drop the task from the open set.
/// </summary>
public sealed record TaskCompleted : PositionEvent
{
    public TaskCompleted(PositionTaskId taskId, DateTimeOffset occurredAt, string? summary = null)
        : base(occurredAt)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        TaskId = taskId;
        Summary = summary is null ? null : CommandText.RequireContent(summary, nameof(summary));
    }

    /// <summary>The identity of the task that was completed.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>An optional outcome summary.</summary>
    public string? Summary { get; }
}
