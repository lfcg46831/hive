using Hive.Domain.Identity;
using System.Text.Json.Serialization;

namespace Hive.Domain.Positions;

/// <summary>
/// Durable scope used to decide whether a short-memory entry is eligible for an AI request.
/// Unscoped entries remain recoverable position state, but are intentionally not prompt context.
/// </summary>
public sealed record ShortMemoryContextScope
{
    public const string ThreadKind = "thread";
    public const string TaskKind = "task";
    public const string DirectiveKind = "directive";
    public const string PositionFactKind = "position-fact";

    public ShortMemoryContextScope(
        string kind,
        ThreadId? threadId = null,
        PositionTaskId? taskId = null,
        DirectiveId? directiveId = null,
        DirectiveId? parentDirectiveId = null)
    {
        ArgumentNullException.ThrowIfNull(kind);

        switch (kind)
        {
            case ThreadKind when threadId is not null && taskId is null &&
                directiveId is null && parentDirectiveId is null:
            case TaskKind when threadId is not null && taskId is not null &&
                directiveId is null && parentDirectiveId is null:
            case DirectiveKind when threadId is not null && directiveId is not null:
            case PositionFactKind when threadId is null && taskId is null &&
                directiveId is null && parentDirectiveId is null:
                break;
            case ThreadKind:
                throw new ArgumentException(
                    "Thread short-memory scope requires a thread id and no task id.",
                    nameof(threadId));
            case TaskKind:
                throw new ArgumentException(
                    "Task short-memory scope requires both thread and task ids.",
                    nameof(taskId));
            case DirectiveKind:
                throw new ArgumentException(
                    "Directive short-memory scope requires thread and directive ids.",
                    nameof(directiveId));
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
        DirectiveId = directiveId;
        ParentDirectiveId = parentDirectiveId;
    }

    public string Kind { get; }

    public ThreadId? ThreadId { get; }

    public PositionTaskId? TaskId { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DirectiveId? DirectiveId { get; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DirectiveId? ParentDirectiveId { get; }

    public static ShortMemoryContextScope ForThread(ThreadId threadId) =>
        new(ThreadKind, threadId ?? throw new ArgumentNullException(nameof(threadId)));

    public static ShortMemoryContextScope ForTask(
        ThreadId threadId,
        PositionTaskId taskId) =>
        new(
            TaskKind,
            threadId ?? throw new ArgumentNullException(nameof(threadId)),
            taskId ?? throw new ArgumentNullException(nameof(taskId)));

    public static ShortMemoryContextScope ForDirective(
        ThreadId threadId,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId = null,
        PositionTaskId? taskId = null) =>
        new(
            DirectiveKind,
            threadId ?? throw new ArgumentNullException(nameof(threadId)),
            taskId,
            directiveId ?? throw new ArgumentNullException(nameof(directiveId)),
            parentDirectiveId);

    public static ShortMemoryContextScope ForPositionFact() => new(PositionFactKind);
}
