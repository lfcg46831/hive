using System.Collections.Immutable;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveSelectedContext
{
    public AiDirectiveSelectedContext(
        ImmutableArray<AiDirectiveShortMemoryEntry> shortMemory,
        ImmutableArray<PersistedTask> openTasks,
        ImmutableArray<MessageId> recentHistory,
        ImmutableArray<OrgMessage> materializedHistory,
        int budgetUtf8Bytes,
        int usedUtf8Bytes)
    {
        if (shortMemory.IsDefault || openTasks.IsDefault || recentHistory.IsDefault ||
            materializedHistory.IsDefault)
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
        MaterializedHistory = materializedHistory;
        BudgetUtf8Bytes = budgetUtf8Bytes;
        UsedUtf8Bytes = usedUtf8Bytes;
    }

    public ImmutableArray<AiDirectiveShortMemoryEntry> ShortMemory { get; }

    public ImmutableArray<PersistedTask> OpenTasks { get; }

    public ImmutableArray<MessageId> RecentHistory { get; }

    public ImmutableArray<OrgMessage> MaterializedHistory { get; }

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
        var selectedMessages = ImmutableArray.CreateBuilder<OrgMessage>();

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
        var relatedTaskMessages = relatedTasks
            .Where(task => task.CausedBy is not null)
            .Select(task => task.CausedBy!)
            .ToHashSet();
        var directiveLineage = BuildDirectiveLineage(context);

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

        var directiveMemory = context.ShortMemory
            .Where(entry => entry.ContextScope is
            {
                Kind: ShortMemoryContextScope.DirectiveKind,
                ThreadId: { } threadId,
                DirectiveId: { } directiveId,
            }
                && threadId == context.Directive.ThreadId
                && (directiveLineage.Contains(directiveId) ||
                    entry.ContextScope.TaskId is { } taskId && relatedTaskIds.Contains(taskId)))
            .OrderBy(entry => entry.ContextScope!.DirectiveId!.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal);
        AddMemory(directiveMemory, selectedMemory, ref remaining);

        var threadMemory = context.ShortMemory
            .Where(entry => entry.ContextScope is
            {
                Kind: ShortMemoryContextScope.ThreadKind,
                ThreadId: { } threadId,
            }
                && threadId == context.Directive.ThreadId)
            .OrderBy(entry => entry.Key, StringComparer.Ordinal);
        AddMemory(threadMemory, selectedMemory, ref remaining);

        var historyOrder = context.RecentHistory
            .Select((messageId, index) => (messageId, index))
            .GroupBy(item => item.messageId)
            .ToDictionary(group => group.Key, group => group.First().index);
        var relatedMessages = context.MaterializedHistory
            .Where(message => IsRelatedMessage(
                context,
                message,
                directiveLineage,
                relatedTaskMessages))
            .OrderBy(message => historyOrder.GetValueOrDefault(message.Id, int.MaxValue))
            .ThenBy(message => message.SentAt)
            .ThenBy(message => message.Id.Value);
        foreach (var message in relatedMessages)
        {
            var line = AiDirectiveContextLines.MaterializedMessage(message);
            var cost = AiDirectiveContextLines.Utf8Cost(line);
            if (cost > remaining)
            {
                continue;
            }

            selectedMessages.Add(message);
            selectedHistory.Add(message.Id);
            remaining -= cost;
        }

        var positionFacts = context.ShortMemory
            .Where(entry => entry.ContextScope is
            {
                Kind: ShortMemoryContextScope.PositionFactKind,
            })
            .OrderBy(entry => entry.Key, StringComparer.Ordinal);
        AddMemory(positionFacts, selectedMemory, ref remaining);

        return new AiDirectiveSelectedContext(
            selectedMemory.ToImmutable(),
            selectedTasks.ToImmutable(),
            selectedHistory.ToImmutable(),
            selectedMessages.ToImmutable(),
            budgetUtf8Bytes,
            budgetUtf8Bytes - remaining);
    }

    private static HashSet<DirectiveId> BuildDirectiveLineage(
        AiDirectiveExecutionContext context)
    {
        var lineage = new HashSet<DirectiveId> { context.Directive.DirectiveId };
        var directives = context.MaterializedHistory
            .OfType<OrgDirective>()
            .Where(message =>
                message.OrganizationId == context.OrganizationId &&
                message.Thread == context.Directive.ThreadId)
            .GroupBy(message => message.DirectiveId)
            .ToDictionary(group => group.Key, group => group.First());

        var parent = context.Directive.ParentDirectiveId;
        while (parent is not null && lineage.Add(parent))
        {
            parent = directives.GetValueOrDefault(parent)?.ParentDirectiveId;
        }

        return lineage;
    }

    private static bool IsRelatedMessage(
        AiDirectiveExecutionContext context,
        OrgMessage message,
        IReadOnlySet<DirectiveId> directiveLineage,
        IReadOnlySet<MessageId> relatedTaskMessages)
    {
        if (message.OrganizationId != context.OrganizationId ||
            message.Thread != context.Directive.ThreadId ||
            message.Id == context.Directive.MessageId)
        {
            return false;
        }

        return message switch
        {
            OrgDirective directive =>
                directiveLineage.Contains(directive.DirectiveId) ||
                relatedTaskMessages.Contains(directive.Id),
            Report report => directiveLineage.Contains(report.AboutDirectiveId),
            _ => true,
        };
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
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static string ShortMemory(AiDirectiveShortMemoryEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return $"- {entry.Key}: {entry.Value}";
    }

    public static string Task(PersistedTask task)
    {
        ArgumentNullException.ThrowIfNull(task);
        return $"- {task.TaskId}: {Json(task.Title)} | Thread: {task.Thread} | Priority: {task.Priority} | Deadline: {ValueOrNone(task.Deadline?.ToString("O"))} | LatestProgress: {JsonOrNone(task.LatestProgress)}";
    }

    public static string RecentHistory(MessageId message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return $"- {message}";
    }

    public static string MaterializedMessage(OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var prefix = $"- Type: {message.GetType().Name} | MessageId: {message.Id} | SentAt: {message.SentAt:O}";
        return message switch
        {
            OrgDirective directive =>
                $"{prefix} | DirectiveId: {directive.DirectiveId} | ParentDirectiveId: {ValueOrNone(directive.ParentDirectiveId?.ToString())} | Objective: {Json(directive.Objective)} | Context: {Json(directive.Context)}",
            Report report =>
                $"{prefix} | AboutDirectiveId: {report.AboutDirectiveId} | Kind: {report.Kind} | Body: {Json(report.Body)}",
            Escalation escalation =>
                $"{prefix} | Issue: {Json(escalation.Issue)} | Context: {Json(escalation.Context)} | OptionsConsidered: {Json(string.Join(" | ", escalation.OptionsConsidered))}",
            Memo memo => $"{prefix} | Body: {Json(memo.Body)}",
            PeerRequest request => $"{prefix} | Ask: {Json(request.Ask)}",
            PeerResponse response =>
                $"{prefix} | InReplyTo: {response.InReplyTo} | Body: {Json(response.Body)}",
            ApprovalRequest request =>
                $"{prefix} | Action: {Json(request.Action)} | Justification: {Json(request.Justification)} | Policy: {request.Policy}",
            ApprovalDecision decision =>
                $"{prefix} | RequestId: {decision.RequestId} | Approved: {decision.Approved} | Reason: {JsonOrNone(decision.Reason)}",
            AuthorizationGrant grant =>
                $"{prefix} | InReplyTo: {grant.InReplyTo} | RetainedActionId: {grant.RetainedActionId} | Key: {grant.Key} | ExpiresAt: {grant.ExpiresAt:O} | Reason: {JsonOrNone(grant.Reason)}",
            AuthorizationDenial denial =>
                $"{prefix} | InReplyTo: {denial.InReplyTo} | RetainedActionId: {denial.RetainedActionId} | Reason: {Json(denial.Reason)}",
            EventTrigger trigger =>
                $"{prefix} | EventType: {Json(trigger.EventType)} | Payload: {Json(trigger.Payload)}",
            Pulse pulse =>
                $"{prefix} | ScheduleId: {Json(pulse.ScheduleId)} | Payload: {Json(pulse.Payload)}",
            _ => $"{prefix} | Content: <not-projected>",
        };
    }

    public static int Utf8Cost(string canonicalLine)
    {
        ArgumentNullException.ThrowIfNull(canonicalLine);
        return Encoding.UTF8.GetByteCount(canonicalLine) + 1;
    }

    private static string ValueOrNone(string? value) => value ?? "<none>";

    private static string Json(string value) => JsonSerializer.Serialize(value, JsonOptions);

    private static string JsonOrNone(string? value) =>
        value is null ? "<none>" : Json(value);
}
