using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Durable scope used to decide whether a short-memory entry is eligible for an AI request.
/// Unscoped entries remain recoverable position state, but are intentionally not prompt context.
/// </summary>
public sealed record ShortMemoryContextScope
{
    public const string ThreadKind = "thread";
    public const string TaskKind = "task";
    public const string PositionFactKind = "position-fact";

    public ShortMemoryContextScope(
        string kind,
        ThreadId? threadId = null,
        PositionTaskId? taskId = null)
    {
        ArgumentNullException.ThrowIfNull(kind);

        switch (kind)
        {
            case ThreadKind when threadId is not null && taskId is null:
            case TaskKind when threadId is not null && taskId is not null:
            case PositionFactKind when threadId is null && taskId is null:
                break;
            case ThreadKind:
                throw new ArgumentException(
                    "Thread short-memory scope requires a thread id and no task id.",
                    nameof(threadId));
            case TaskKind:
                throw new ArgumentException(
                    "Task short-memory scope requires both thread and task ids.",
                    nameof(taskId));
            case PositionFactKind:
                throw new ArgumentException(
                    "Position-fact short-memory scope cannot carry thread or task ids.",
                    nameof(threadId));
            default:
                throw new ArgumentException(
                    $"Unknown short-memory context scope kind '{kind}'.",
                    nameof(kind));
        }

        Kind = kind;
        ThreadId = threadId;
        TaskId = taskId;
    }

    public string Kind { get; }

    public ThreadId? ThreadId { get; }

    public PositionTaskId? TaskId { get; }

    public static ShortMemoryContextScope ForThread(ThreadId threadId) =>
        new(ThreadKind, threadId ?? throw new ArgumentNullException(nameof(threadId)));

    public static ShortMemoryContextScope ForTask(
        ThreadId threadId,
        PositionTaskId taskId) =>
        new(
            TaskKind,
            threadId ?? throw new ArgumentNullException(nameof(threadId)),
            taskId ?? throw new ArgumentNullException(nameof(taskId)));

    public static ShortMemoryContextScope ForPositionFact() => new(PositionFactKind);
}
