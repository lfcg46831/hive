using System.Collections.Immutable;
using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveSelectedContext
{
    public AiDirectiveSelectedContext(
        ImmutableArray<AiDirectiveShortMemoryEntry> shortMemory,
        ImmutableArray<PersistedTask> openTasks,
        ImmutableArray<MessageId> recentHistory,
        int budgetUtf8Bytes,
        int usedUtf8Bytes)
    {
        if (shortMemory.IsDefault || openTasks.IsDefault || recentHistory.IsDefault)
        {
            throw new ArgumentException("Selected context collections cannot be default.");
        }

        if (budgetUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(budgetUtf8Bytes));
        }

        if (usedUtf8Bytes < 0 || usedUtf8Bytes > budgetUtf8Bytes)
        {
            throw new ArgumentOutOfRangeException(nameof(usedUtf8Bytes));
        }

        ShortMemory = shortMemory;
        OpenTasks = openTasks;
        RecentHistory = recentHistory;
        BudgetUtf8Bytes = budgetUtf8Bytes;
        UsedUtf8Bytes = usedUtf8Bytes;
    }

    public ImmutableArray<AiDirectiveShortMemoryEntry> ShortMemory { get; }

    public ImmutableArray<PersistedTask> OpenTasks { get; }

    public ImmutableArray<MessageId> RecentHistory { get; }

    public int BudgetUtf8Bytes { get; }

    public int UsedUtf8Bytes { get; }
}

internal static class AiDirectiveContextSelector
{
    public const int DefaultBudgetUtf8Bytes = 4096;

    public static AiDirectiveSelectedContext Select(
        AiDirectiveExecutionContext context,
        int budgetUtf8Bytes = DefaultBudgetUtf8Bytes)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (budgetUtf8Bytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(budgetUtf8Bytes),
                budgetUtf8Bytes,
                "AI directive context budget must be greater than zero.");
        }

        var remaining = budgetUtf8Bytes;
        var selectedTasks = ImmutableArray.CreateBuilder<PersistedTask>();
        var selectedMemory = ImmutableArray.CreateBuilder<AiDirectiveShortMemoryEntry>();
        var selectedHistory = ImmutableArray.CreateBuilder<MessageId>();

        var relatedTasks = context.OpenTasks
            .Where(task => task.Thread == context.Directive.ThreadId)
            .OrderBy(task => task.CausedBy == context.Directive.MessageId ? 0 : 1)
            .ThenBy(task => task.TaskId.Value)
            .ToImmutableArray();

        foreach (var task in relatedTasks)
        {
            TryAdd(task, AiDirectiveContextLines.Task(task), selectedTasks, ref remaining);
        }

        var relatedTaskIds = relatedTasks
            .Select(task => task.TaskId)
            .ToHashSet();

        var taskMemory = context.ShortMemory
            .Where(entry => entry.ContextScope is
            {
                Kind: ShortMemoryContextScope.TaskKind,
                ThreadId: { } threadId,
                TaskId: { } taskId,
            }
                && threadId == context.Directive.ThreadId
                && relatedTaskIds.Contains(taskId))
            .OrderBy(entry => entry.ContextScope!.TaskId!.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal);
        AddMemory(taskMemory, selectedMemory, ref remaining);

        var threadMemory = context.ShortMemory
            .Where(entry => entry.ContextScope is
            {
                Kind: ShortMemoryContextScope.ThreadKind,
                ThreadId: { } threadId,
            }
                && threadId == context.Directive.ThreadId)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal);
        AddMemory(threadMemory, selectedMemory, ref remaining);

        var positionFacts = context.ShortMemory
            .Where(entry => entry.ContextScope is
            {
                Kind: ShortMemoryContextScope.PositionFactKind,
            })
            .OrderBy(entry => entry.Key, StringComparer.Ordinal);
        AddMemory(positionFacts, selectedMemory, ref remaining);

        foreach (var message in context.RecentHistory.Where(
                     message => message == context.Directive.MessageId))
        {
            TryAdd(
                message,
                AiDirectiveContextLines.RecentHistory(message),
                selectedHistory,
                ref remaining);
        }

        return new AiDirectiveSelectedContext(
            selectedMemory.ToImmutable(),
            selectedTasks.ToImmutable(),
            selectedHistory.ToImmutable(),
            budgetUtf8Bytes,
            budgetUtf8Bytes - remaining);
    }

    private static void AddMemory(
        IEnumerable<AiDirectiveShortMemoryEntry> candidates,
        ImmutableArray<AiDirectiveShortMemoryEntry>.Builder selected,
        ref int remaining)
    {
        foreach (var entry in candidates)
        {
            TryAdd(entry, AiDirectiveContextLines.ShortMemory(entry), selected, ref remaining);
        }
    }

    private static void TryAdd<T>(
        T value,
        string canonicalLine,
        ImmutableArray<T>.Builder selected,
        ref int remaining)
    {
        var cost = AiDirectiveContextLines.Utf8Cost(canonicalLine);
        if (cost > remaining)
        {
            return;
        }

        selected.Add(value);
        remaining -= cost;
    }
}

internal static class AiDirectiveContextLines
{
    public static string ShortMemory(AiDirectiveShortMemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"- {entry.Key}: {entry.Value}";
    }

    public static string Task(PersistedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return $"- {task.TaskId}: {task.Title} | Thread: {task.Thread} | Priority: {task.Priority} | Deadline: {ValueOrNone(task.Deadline?.ToString("O"))}";
    }

    public static string RecentHistory(MessageId message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"- {message}";
    }

    public static int Utf8Cost(string canonicalLine)
    {
        ArgumentNullException.ThrowIfNull(canonicalLine);
        return Encoding.UTF8.GetByteCount(canonicalLine) + 1;
    }

    private static string ValueOrNone(string? value) => value ?? "<none>";
}
