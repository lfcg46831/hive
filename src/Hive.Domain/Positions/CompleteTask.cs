using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Mark an open task as concluded. <see cref="Summary"/> optionally captures the outcome; when
/// provided it must carry content. Whether the referenced task exists and is still open is validated
/// by the entity against its state, not by this contract.
/// </summary>
public sealed record CompleteTask : PositionCommand
{
    public CompleteTask(PositionTaskId taskId, string? summary = null)
    {
        ArgumentNullException.ThrowIfNull(taskId);

        TaskId = taskId;
        Summary = summary is null ? null : CommandText.RequireContent(summary, nameof(summary));
    }

    /// <summary>The identity of the task to complete.</summary>
    public PositionTaskId TaskId { get; }

    /// <summary>An optional outcome summary.</summary>
    public string? Summary { get; }
}
