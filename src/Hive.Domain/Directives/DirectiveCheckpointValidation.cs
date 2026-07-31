using System.Collections.Immutable;
using Hive.Domain.Outcomes;

namespace Hive.Domain.Directives;

public enum DirectiveCheckpointValidationCode
{
    UnsupportedCheckpointVersion = 1,
    UnsupportedPlanVersion = 2,
    CorrelationMismatch = 3,
    CompletedSubtaskUnknown = 4,
    EvidenceUngrounded = 5,
    NextSubtaskUnknown = 6,
    NoCompletedSubtask = 7,
    NextSubtaskMissing = 8,
    NextSubtaskAlreadyCompleted = 9,
    BlockersPresent = 10,
    ResponsibilityNotRetained = 11,
    InterventionRequired = 12,
    ProjectionBudgetExceeded = 13,
}

public static class DirectiveCheckpointValidationCodeContract
{
    public static DirectiveCheckpointValidationCode RequireDefined(
        DirectiveCheckpointValidationCode value,
        string parameterName) => value switch
        {
            DirectiveCheckpointValidationCode.UnsupportedCheckpointVersion or
            DirectiveCheckpointValidationCode.UnsupportedPlanVersion or
            DirectiveCheckpointValidationCode.CorrelationMismatch or
            DirectiveCheckpointValidationCode.CompletedSubtaskUnknown or
            DirectiveCheckpointValidationCode.EvidenceUngrounded or
            DirectiveCheckpointValidationCode.NextSubtaskUnknown or
            DirectiveCheckpointValidationCode.NoCompletedSubtask or
            DirectiveCheckpointValidationCode.NextSubtaskMissing or
            DirectiveCheckpointValidationCode.NextSubtaskAlreadyCompleted or
            DirectiveCheckpointValidationCode.BlockersPresent or
            DirectiveCheckpointValidationCode.ResponsibilityNotRetained or
            DirectiveCheckpointValidationCode.InterventionRequired or
            DirectiveCheckpointValidationCode.ProjectionBudgetExceeded => value,
            _ => throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Directive checkpoint validation code is undefined."),
        };

    public static string ToWireValue(DirectiveCheckpointValidationCode value) =>
        RequireDefined(value, nameof(value)) switch
        {
            DirectiveCheckpointValidationCode.UnsupportedCheckpointVersion =>
                "unsupported-checkpoint-version",
            DirectiveCheckpointValidationCode.UnsupportedPlanVersion =>
                "unsupported-plan-version",
            DirectiveCheckpointValidationCode.CorrelationMismatch => "correlation-mismatch",
            DirectiveCheckpointValidationCode.CompletedSubtaskUnknown =>
                "completed-subtask-unknown",
            DirectiveCheckpointValidationCode.EvidenceUngrounded => "evidence-ungrounded",
            DirectiveCheckpointValidationCode.NextSubtaskUnknown => "next-subtask-unknown",
            DirectiveCheckpointValidationCode.NoCompletedSubtask => "no-completed-subtask",
            DirectiveCheckpointValidationCode.NextSubtaskMissing => "next-subtask-missing",
            DirectiveCheckpointValidationCode.NextSubtaskAlreadyCompleted =>
                "next-subtask-already-completed",
            DirectiveCheckpointValidationCode.BlockersPresent => "blockers-present",
            DirectiveCheckpointValidationCode.ResponsibilityNotRetained =>
                "responsibility-not-retained",
            DirectiveCheckpointValidationCode.InterventionRequired =>
                "intervention-required",
            DirectiveCheckpointValidationCode.ProjectionBudgetExceeded =>
                "projection-budget-exceeded",
            _ => throw new InvalidOperationException(
                "Validated directive checkpoint code is not mapped."),
        };
}

public sealed record DirectiveCheckpointValidationError
{
    public DirectiveCheckpointValidationError(
        DirectiveCheckpointValidationCode code,
        string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Code = DirectiveCheckpointValidationCodeContract.RequireDefined(code, nameof(code));
        Path = path.Trim();
    }

    public DirectiveCheckpointValidationCode Code { get; }

    public string Path { get; }
}

public sealed record DirectiveCheckpointValidationResult
{
    private DirectiveCheckpointValidationResult(
        ImmutableArray<DirectiveCheckpointValidationError> errors)
    {
        Errors = errors;
    }

    public static DirectiveCheckpointValidationResult Valid { get; } = new([]);

    public ImmutableArray<DirectiveCheckpointValidationError> Errors { get; }

    public bool IsValid => Errors.IsEmpty;

    public static DirectiveCheckpointValidationResult Create(
        IEnumerable<DirectiveCheckpointValidationError> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);
        var snapshot = errors.ToImmutableArray();
        if (snapshot.Any(error => error is null))
        {
            throw new ArgumentException(
                "Directive checkpoint validation errors cannot contain null entries.",
                nameof(errors));
        }

        if (snapshot.IsEmpty)
        {
            return Valid;
        }

        return new DirectiveCheckpointValidationResult(
            snapshot
                .Distinct()
                .OrderBy(error => error.Path, StringComparer.Ordinal)
                .ThenBy(
                    error => DirectiveCheckpointValidationCodeContract.ToWireValue(error.Code),
                    StringComparer.Ordinal)
                .ToImmutableArray());
    }
}

/// <summary>
/// Runtime-owned facts used to validate a checkpoint. None are inferred from provider prose.
/// </summary>
public sealed record DirectiveCheckpointValidationContext
{
    public DirectiveCheckpointValidationContext(
        DirectiveCheckpointCorrelation expectedCorrelation,
        DirectiveCheckpointEvidenceContext evidence,
        bool responsibilityRetained,
        OutcomeRequiredIntervention requiredIntervention)
    {
        ExpectedCorrelation = expectedCorrelation ??
            throw new ArgumentNullException(nameof(expectedCorrelation));
        Evidence = evidence ?? throw new ArgumentNullException(nameof(evidence));
        ResponsibilityRetained = responsibilityRetained;
        RequiredIntervention = OutcomeRequiredInterventionContract.RequireDefined(
            requiredIntervention,
            nameof(requiredIntervention));
    }

    public DirectiveCheckpointCorrelation ExpectedCorrelation { get; }

    public DirectiveCheckpointEvidenceContext Evidence { get; }

    public bool ResponsibilityRetained { get; }

    public OutcomeRequiredIntervention RequiredIntervention { get; }
}

public static class DirectiveCheckpointValidator
{
    public static DirectiveCheckpointValidationResult Validate(
        DirectiveCheckpoint checkpoint,
        DirectiveCheckpointValidationContext context) =>
        ValidateCore(checkpoint, context, requireProgressReady: false);

    public static DirectiveCheckpointValidationResult ValidateForProgress(
        DirectiveCheckpoint checkpoint,
        DirectiveCheckpointValidationContext context) =>
        ValidateCore(checkpoint, context, requireProgressReady: true);

    private static DirectiveCheckpointValidationResult ValidateCore(
        DirectiveCheckpoint checkpoint,
        DirectiveCheckpointValidationContext context,
        bool requireProgressReady)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);
        ArgumentNullException.ThrowIfNull(context);

        var errors = ImmutableArray.CreateBuilder<DirectiveCheckpointValidationError>();
        if (checkpoint.ContractVersion != DirectiveCheckpointContractVersions.V1)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.UnsupportedCheckpointVersion,
                "contract_version");
        }

        if (checkpoint.Plan.ContractVersion != DirectiveCheckpointContractVersions.V1)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.UnsupportedPlanVersion,
                "plan.contract_version");
        }

        ValidateCorrelation(checkpoint.Correlation, context.ExpectedCorrelation, errors);

        var planIds = checkpoint.Plan.Subtasks
            .Select(subtask => subtask.LocalId)
            .ToHashSet(StringComparer.Ordinal);
        for (var completedIndex = 0;
             completedIndex < checkpoint.CompletedSubtasks.Length;
             completedIndex++)
        {
            var completed = checkpoint.CompletedSubtasks[completedIndex];
            var completedPath = $"completed_subtasks[{completedIndex}]";
            if (!planIds.Contains(completed.LocalId))
            {
                Add(
                    errors,
                    DirectiveCheckpointValidationCode.CompletedSubtaskUnknown,
                    $"{completedPath}.local_id");
            }

            for (var evidenceIndex = 0;
                 evidenceIndex < completed.EvidenceReferences.Length;
                 evidenceIndex++)
            {
                if (!context.Evidence.Allows(completed.EvidenceReferences[evidenceIndex]))
                {
                    Add(
                        errors,
                        DirectiveCheckpointValidationCode.EvidenceUngrounded,
                        $"{completedPath}.evidence_references[{evidenceIndex}]");
                }
            }
        }

        if (checkpoint.NextSubtaskId is { } nextSubtaskId && !planIds.Contains(nextSubtaskId))
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.NextSubtaskUnknown,
                "next_subtask_id");
        }

        if (!DirectiveCheckpointContextProjector.TryProject(checkpoint, out _))
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.ProjectionBudgetExceeded,
                "$projection");
        }

        if (requireProgressReady)
        {
            ValidateProgressReadiness(checkpoint, context, errors);
        }

        return DirectiveCheckpointValidationResult.Create(errors);
    }

    private static void ValidateCorrelation(
        DirectiveCheckpointCorrelation actual,
        DirectiveCheckpointCorrelation expected,
        ImmutableArray<DirectiveCheckpointValidationError>.Builder errors)
    {
        CorrelationField(actual.OrganizationId, expected.OrganizationId, "organization_id", errors);
        CorrelationField(actual.PositionId, expected.PositionId, "position_id", errors);
        CorrelationField(actual.ThreadId, expected.ThreadId, "thread_id", errors);
        CorrelationField(actual.DirectiveId, expected.DirectiveId, "directive_id", errors);
        CorrelationField(
            actual.ParentDirectiveId,
            expected.ParentDirectiveId,
            "parent_directive_id",
            errors);
        CorrelationField(
            actual.PositionTaskId,
            expected.PositionTaskId,
            "position_task_id",
            errors);
    }

    private static void CorrelationField<T>(
        T actual,
        T expected,
        string field,
        ImmutableArray<DirectiveCheckpointValidationError>.Builder errors)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.CorrelationMismatch,
                $"correlation.{field}");
        }
    }

    private static void ValidateProgressReadiness(
        DirectiveCheckpoint checkpoint,
        DirectiveCheckpointValidationContext context,
        ImmutableArray<DirectiveCheckpointValidationError>.Builder errors)
    {
        if (checkpoint.CompletedSubtasks.IsEmpty)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.NoCompletedSubtask,
                "completed_subtasks");
        }

        if (!checkpoint.Blockers.IsEmpty)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.BlockersPresent,
                "blockers");
        }

        if (checkpoint.NextSubtaskId is null)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.NextSubtaskMissing,
                "next_subtask_id");
        }
        else if (checkpoint.CompletedSubtasks.Any(completed =>
                     string.Equals(
                         completed.LocalId,
                         checkpoint.NextSubtaskId,
                         StringComparison.Ordinal)))
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.NextSubtaskAlreadyCompleted,
                "next_subtask_id");
        }

        if (!context.ResponsibilityRetained)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.ResponsibilityNotRetained,
                "$runtime.responsibility_retained");
        }

        if (context.RequiredIntervention != OutcomeRequiredIntervention.None)
        {
            Add(
                errors,
                DirectiveCheckpointValidationCode.InterventionRequired,
                "$runtime.required_intervention");
        }
    }

    private static void Add(
        ImmutableArray<DirectiveCheckpointValidationError>.Builder errors,
        DirectiveCheckpointValidationCode code,
        string path) => errors.Add(new DirectiveCheckpointValidationError(code, path));
}
