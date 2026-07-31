using System.Collections.Immutable;
using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Domain.Directives;

public static class DirectiveCheckpointContractVersions
{
    public const int V1 = 1;
}

public static class DirectiveCheckpointContractLimits
{
    public const int MaximumSubtasks = 16;
    public const int MaximumCompletionCriteriaPerSubtask = 8;
    public const int MaximumCompletedSubtasks = MaximumSubtasks;
    public const int MaximumEvidenceReferencesPerCompletion = 8;
    public const int MaximumEvidenceContextReferences = 32;
    public const int MaximumBlockers = 8;
    public const int MaximumLocalIdCharacters = 64;
    public const int MaximumObjectiveUtf8Bytes = 512;
    public const int MaximumCompletionCriterionUtf8Bytes = 256;
    public const int MaximumContextProjectionUtf8Bytes = 4096;

    public static readonly TimeSpan MaximumEstimatedDuration = TimeSpan.FromDays(7);
}

/// <summary>
/// One position-local unit of work. It deliberately has no organizational target or directive
/// payload: delegation remains an organizational message, not an internal plan transition.
/// </summary>
public sealed record DirectiveCheckpointSubtask
{
    public DirectiveCheckpointSubtask(
        int sequence,
        string localId,
        string objective,
        IEnumerable<string> completionCriteria,
        TimeSpan estimatedDuration)
    {
        if (sequence <= 0 || sequence > DirectiveCheckpointContractLimits.MaximumSubtasks)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                $"Subtask sequence must be between 1 and {DirectiveCheckpointContractLimits.MaximumSubtasks}.");
        }

        if (estimatedDuration <= TimeSpan.Zero ||
            estimatedDuration > DirectiveCheckpointContractLimits.MaximumEstimatedDuration)
        {
            throw new ArgumentOutOfRangeException(
                nameof(estimatedDuration),
                estimatedDuration,
                $"Estimated duration must be positive and no greater than {DirectiveCheckpointContractLimits.MaximumEstimatedDuration}.");
        }

        ArgumentNullException.ThrowIfNull(completionCriteria);
        var criteria = completionCriteria
            .Select(criterion => DirectiveCheckpointContractGuards.RequireUtf8Text(
                criterion,
                DirectiveCheckpointContractLimits.MaximumCompletionCriterionUtf8Bytes,
                nameof(completionCriteria)))
            .ToImmutableArray();
        if (criteria.IsEmpty ||
            criteria.Length > DirectiveCheckpointContractLimits.MaximumCompletionCriteriaPerSubtask)
        {
            throw new ArgumentException(
                $"A subtask must contain between 1 and {DirectiveCheckpointContractLimits.MaximumCompletionCriteriaPerSubtask} completion criteria.",
                nameof(completionCriteria));
        }

        if (criteria.Distinct(StringComparer.Ordinal).Count() != criteria.Length)
        {
            throw new ArgumentException(
                "Subtask completion criteria must be unique.",
                nameof(completionCriteria));
        }

        Sequence = sequence;
        LocalId = DirectiveCheckpointContractGuards.RequireLocalId(localId, nameof(localId));
        Objective = DirectiveCheckpointContractGuards.RequireUtf8Text(
            objective,
            DirectiveCheckpointContractLimits.MaximumObjectiveUtf8Bytes,
            nameof(objective));
        CompletionCriteria = criteria
            .OrderBy(criterion => criterion, StringComparer.Ordinal)
            .ToImmutableArray();
        EstimatedDuration = estimatedDuration;
    }

    public int Sequence { get; }

    public string LocalId { get; }

    public string Objective { get; }

    public ImmutableArray<string> CompletionCriteria { get; }

    /// <summary>
    /// Advisory model estimate only. Runtime checkpoint decisions use actual remaining time.
    /// </summary>
    public TimeSpan EstimatedDuration { get; }
}

public sealed record DirectiveCheckpointPlan
{
    public DirectiveCheckpointPlan(
        int contractVersion,
        IEnumerable<DirectiveCheckpointSubtask> subtasks)
    {
        if (contractVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                "Checkpoint plan contract version must be positive.");
        }

        ArgumentNullException.ThrowIfNull(subtasks);
        var snapshot = subtasks.ToImmutableArray();
        if (snapshot.IsEmpty || snapshot.Length > DirectiveCheckpointContractLimits.MaximumSubtasks)
        {
            throw new ArgumentException(
                $"A checkpoint plan must contain between 1 and {DirectiveCheckpointContractLimits.MaximumSubtasks} subtasks.",
                nameof(subtasks));
        }

        if (snapshot.Any(subtask => subtask is null))
        {
            throw new ArgumentException(
                "A checkpoint plan cannot contain null subtasks.",
                nameof(subtasks));
        }

        if (snapshot.Select(subtask => subtask.LocalId)
            .Distinct(StringComparer.Ordinal)
            .Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Checkpoint plan local subtask ids must be unique.",
                nameof(subtasks));
        }

        var ordered = snapshot.OrderBy(subtask => subtask.Sequence).ToImmutableArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            if (ordered[index].Sequence != index + 1)
            {
                throw new ArgumentException(
                    "Checkpoint plan subtask sequences must be unique and contiguous from 1.",
                    nameof(subtasks));
            }
        }

        ContractVersion = contractVersion;
        Subtasks = ordered;
    }

    public int ContractVersion { get; }

    public ImmutableArray<DirectiveCheckpointSubtask> Subtasks { get; }
}

public sealed record CompletedDirectiveCheckpointSubtask
{
    public CompletedDirectiveCheckpointSubtask(
        string localId,
        IEnumerable<OutcomeEvidenceReference> evidenceReferences)
    {
        ArgumentNullException.ThrowIfNull(evidenceReferences);
        var snapshot = evidenceReferences.ToImmutableArray();
        if (snapshot.IsEmpty ||
            snapshot.Length >
            DirectiveCheckpointContractLimits.MaximumEvidenceReferencesPerCompletion)
        {
            throw new ArgumentException(
                $"A completed subtask must contain between 1 and {DirectiveCheckpointContractLimits.MaximumEvidenceReferencesPerCompletion} evidence references.",
                nameof(evidenceReferences));
        }

        if (snapshot.Any(reference => reference is null))
        {
            throw new ArgumentException(
                "Completed-subtask evidence cannot contain null references.",
                nameof(evidenceReferences));
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Completed-subtask evidence references must be unique.",
                nameof(evidenceReferences));
        }

        LocalId = DirectiveCheckpointContractGuards.RequireLocalId(localId, nameof(localId));
        EvidenceReferences = snapshot
            .OrderBy(
                reference => OutcomeEvidenceSourceContract.ToWireValue(reference.Source),
                StringComparer.Ordinal)
            .ThenBy(reference => reference.Reference, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public string LocalId { get; }

    public ImmutableArray<OutcomeEvidenceReference> EvidenceReferences { get; }
}

public sealed record DirectiveCheckpointCorrelation
{
    public DirectiveCheckpointCorrelation(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId = null,
        PositionTaskId? positionTaskId = null)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        if (parentDirectiveId == directiveId)
        {
            throw new ArgumentException(
                "A directive cannot be its own checkpoint parent.",
                nameof(parentDirectiveId));
        }

        ParentDirectiveId = parentDirectiveId;
        PositionTaskId = positionTaskId;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId DirectiveId { get; }

    public DirectiveId? ParentDirectiveId { get; }

    public PositionTaskId? PositionTaskId { get; }
}

/// <summary>
/// Provider-neutral, bounded checkpoint value. It carries references to evidence, never evidence
/// payloads, provider output, prompts, reasoning, attachments, or executable handles.
/// </summary>
public sealed record DirectiveCheckpoint
{
    public DirectiveCheckpoint(
        int contractVersion,
        int revision,
        DirectiveCheckpointPlan plan,
        DirectiveCheckpointCorrelation correlation,
        IEnumerable<CompletedDirectiveCheckpointSubtask>? completedSubtasks = null,
        IEnumerable<OutcomeBlocker>? blockers = null,
        string? nextSubtaskId = null)
    {
        if (contractVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                "Directive checkpoint contract version must be positive.");
        }

        if (revision <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(revision),
                revision,
                "Directive checkpoint revision must be positive.");
        }

        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        Correlation = correlation ?? throw new ArgumentNullException(nameof(correlation));

        var completed = (completedSubtasks ?? []).ToImmutableArray();
        if (completed.Length > DirectiveCheckpointContractLimits.MaximumCompletedSubtasks)
        {
            throw new ArgumentException(
                $"A directive checkpoint cannot contain more than {DirectiveCheckpointContractLimits.MaximumCompletedSubtasks} completed subtasks.",
                nameof(completedSubtasks));
        }

        if (completed.Any(subtask => subtask is null))
        {
            throw new ArgumentException(
                "A directive checkpoint cannot contain null completed subtasks.",
                nameof(completedSubtasks));
        }

        if (completed.Select(subtask => subtask.LocalId)
            .Distinct(StringComparer.Ordinal)
            .Count() != completed.Length)
        {
            throw new ArgumentException(
                "Completed checkpoint subtask ids must be unique.",
                nameof(completedSubtasks));
        }

        var blockerSnapshot = (blockers ?? [])
            .Select(blocker => OutcomeBlockerContract.RequireDefined(blocker, nameof(blockers)))
            .ToImmutableArray();
        if (blockerSnapshot.Length > DirectiveCheckpointContractLimits.MaximumBlockers)
        {
            throw new ArgumentException(
                $"A directive checkpoint cannot contain more than {DirectiveCheckpointContractLimits.MaximumBlockers} blockers.",
                nameof(blockers));
        }

        if (blockerSnapshot.Distinct().Count() != blockerSnapshot.Length)
        {
            throw new ArgumentException(
                "Directive checkpoint blockers must be unique.",
                nameof(blockers));
        }

        var planSequence = plan.Subtasks.ToDictionary(
            subtask => subtask.LocalId,
            subtask => subtask.Sequence,
            StringComparer.Ordinal);
        ContractVersion = contractVersion;
        Revision = revision;
        CompletedSubtasks = completed
            .OrderBy(subtask => planSequence.GetValueOrDefault(subtask.LocalId, int.MaxValue))
            .ThenBy(subtask => subtask.LocalId, StringComparer.Ordinal)
            .ToImmutableArray();
        Blockers = blockerSnapshot.Order().ToImmutableArray();
        NextSubtaskId = nextSubtaskId is null
            ? null
            : DirectiveCheckpointContractGuards.RequireLocalId(
                nextSubtaskId,
                nameof(nextSubtaskId));
    }

    public int ContractVersion { get; }

    public int Revision { get; }

    public DirectiveCheckpointPlan Plan { get; }

    public DirectiveCheckpointCorrelation Correlation { get; }

    public ImmutableArray<CompletedDirectiveCheckpointSubtask> CompletedSubtasks { get; }

    public ImmutableArray<OutcomeBlocker> Blockers { get; }

    public string? NextSubtaskId { get; }
}

public sealed record DirectiveCheckpointEvidenceContext
{
    public DirectiveCheckpointEvidenceContext(
        IEnumerable<OutcomeEvidenceReference>? allowedReferences)
    {
        var snapshot = (allowedReferences ?? []).ToImmutableArray();
        if (snapshot.Any(reference => reference is null))
        {
            throw new ArgumentException(
                "Checkpoint evidence context cannot contain null references.",
                nameof(allowedReferences));
        }

        if (snapshot.Length > DirectiveCheckpointContractLimits.MaximumEvidenceContextReferences)
        {
            throw new ArgumentException(
                $"Checkpoint evidence context cannot contain more than {DirectiveCheckpointContractLimits.MaximumEvidenceContextReferences} references.",
                nameof(allowedReferences));
        }

        if (snapshot.Distinct().Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Checkpoint evidence context references must be unique.",
                nameof(allowedReferences));
        }

        AllowedReferences = snapshot
            .OrderBy(
                reference => OutcomeEvidenceSourceContract.ToWireValue(reference.Source),
                StringComparer.Ordinal)
            .ThenBy(reference => reference.Reference, StringComparer.Ordinal)
            .ToImmutableArray();
    }

    public ImmutableArray<OutcomeEvidenceReference> AllowedReferences { get; }

    public bool Allows(OutcomeEvidenceReference reference)
    {
        ArgumentNullException.ThrowIfNull(reference);
        return AllowedReferences.Contains(reference);
    }
}

internal static class DirectiveCheckpointContractGuards
{
    public static string RequireLocalId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0 ||
            normalized.Length > DirectiveCheckpointContractLimits.MaximumLocalIdCharacters ||
            !normalized.All(character =>
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'))
        {
            throw new ArgumentException(
                $"Local id must contain 1 to {DirectiveCheckpointContractLimits.MaximumLocalIdCharacters} ASCII letters, digits, '.', '_', or '-'.",
                parameterName);
        }

        return normalized;
    }

    public static string RequireUtf8Text(
        string value,
        int maximumUtf8Bytes,
        string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (Encoding.UTF8.GetByteCount(normalized) > maximumUtf8Bytes)
        {
            throw new ArgumentException(
                $"Value cannot exceed {maximumUtf8Bytes} UTF-8 bytes.",
                parameterName);
        }

        return normalized;
    }
}
