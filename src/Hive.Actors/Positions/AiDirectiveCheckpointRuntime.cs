using Hive.Application.Directives;
using Hive.Domain.Directives;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

internal sealed record AiDirectiveCheckpointMaterialization
{
    private AiDirectiveCheckpointMaterialization(
        DirectiveCheckpoint? checkpoint,
        string? failureCode)
    {
        Checkpoint = checkpoint;
        FailureCode = failureCode;
    }

    public DirectiveCheckpoint? Checkpoint { get; }

    public string? FailureCode { get; }

    public bool IsValid => Checkpoint is not null;

    public static AiDirectiveCheckpointMaterialization Valid(
        DirectiveCheckpoint checkpoint) =>
        new(checkpoint ?? throw new ArgumentNullException(nameof(checkpoint)), null);

    public static AiDirectiveCheckpointMaterialization Invalid(string failureCode) =>
        new(null, AiAgentGatewayText.Require(failureCode, nameof(failureCode)));
}

internal static class AiDirectiveCheckpointRuntime
{
    public const string MissingCode = "checkpoint-required";
    public const string InvalidCode = "checkpoint-invalid";
    public const string ContinuityCode = "checkpoint-continuity-invalid";

    public static AiDirectiveCheckpointMaterialization Materialize(
        AiDirectiveExecutionContext context,
        AiDirectiveReportDecision decision,
        OutcomeProposal? proposal,
        DirectiveCheckpoint? workingCheckpoint = null)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(decision);
        if (decision.Kind != Hive.Domain.Messaging.ReportKind.Progress ||
            decision.Checkpoint is null)
        {
            return AiDirectiveCheckpointMaterialization.Invalid(MissingCode);
        }

        var persistedCurrent = context.ResumeCheckpoint is { } resume &&
            resume.Correlation.DirectiveId == context.Directive.DirectiveId
                ? resume
                : null;
        var revision = workingCheckpoint?.Revision ?? persistedCurrent?.Revision + 1 ?? 1;
        DirectiveCheckpoint checkpoint;
        try
        {
            checkpoint = decision.Checkpoint.Materialize(context, revision);
        }
        catch (ArgumentException)
        {
            return AiDirectiveCheckpointMaterialization.Invalid(InvalidCode);
        }

        var allowedEvidence = AiDirectiveOutcomeEvidenceContext
            .CreateProposalContext(context)
            .DirectiveInputReferences
            .Select(reference => new OutcomeEvidenceReference(
                OutcomeEvidenceSource.DirectiveInput,
                reference));
        var validation = DirectiveCheckpointValidator.ValidateForProgress(
            checkpoint,
            new DirectiveCheckpointValidationContext(
                checkpoint.Correlation,
                new DirectiveCheckpointEvidenceContext(allowedEvidence),
                responsibilityRetained: proposal?.ProposedIntent is not
                    OutcomeProposedIntent.Directive,
                proposal?.RequiredIntervention ?? OutcomeRequiredIntervention.None));
        if (!validation.IsValid)
        {
            return AiDirectiveCheckpointMaterialization.Invalid(InvalidCode);
        }

        var continuityBase = workingCheckpoint ?? context.ResumeCheckpoint;
        if (continuityBase is not null &&
            (!PlansEqual(continuityBase.Plan, checkpoint.Plan) ||
             !RetainsCompletedSubtasks(continuityBase, checkpoint) ||
             !ContainsTransition(continuityBase, checkpoint)))
        {
            return AiDirectiveCheckpointMaterialization.Invalid(ContinuityCode);
        }

        return AiDirectiveCheckpointMaterialization.Valid(checkpoint);
    }

    public static bool ShouldContinue(
        AiDirectiveExecutionContext context,
        ExecutionBudget budget,
        DateTimeOffset observedAt,
        OutcomeProposal? proposal)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(budget);
        return proposal?.ProposedIntent == OutcomeProposedIntent.ContinueWork &&
            context.ExecutionPolicy.AllowsProgressReports &&
            context.ExecutionPolicy.CheckpointLeadTime is { } leadTime &&
            budget.RemainingTime(observedAt) is { } remaining &&
            remaining > leadTime;
    }

    private static bool PlansEqual(
        DirectiveCheckpointPlan left,
        DirectiveCheckpointPlan right) =>
        left.ContractVersion == right.ContractVersion &&
        left.Subtasks.Length == right.Subtasks.Length &&
        left.Subtasks.Zip(right.Subtasks).All(pair =>
            pair.Item1.Sequence == pair.Item2.Sequence &&
            string.Equals(pair.Item1.LocalId, pair.Item2.LocalId, StringComparison.Ordinal) &&
            string.Equals(pair.Item1.Objective, pair.Item2.Objective, StringComparison.Ordinal) &&
            pair.Item1.EstimatedDuration == pair.Item2.EstimatedDuration &&
            pair.Item1.CompletionCriteria.SequenceEqual(
                pair.Item2.CompletionCriteria,
                StringComparer.Ordinal));

    private static bool RetainsCompletedSubtasks(
        DirectiveCheckpoint existing,
        DirectiveCheckpoint candidate)
    {
        var candidateById = candidate.CompletedSubtasks.ToDictionary(
            completed => completed.LocalId,
            StringComparer.Ordinal);
        return existing.CompletedSubtasks.All(completed =>
            candidateById.TryGetValue(completed.LocalId, out var retained) &&
            completed.EvidenceReferences.SequenceEqual(retained.EvidenceReferences));
    }

    private static bool ContainsTransition(
        DirectiveCheckpoint existing,
        DirectiveCheckpoint candidate) =>
        candidate.CompletedSubtasks.Length > existing.CompletedSubtasks.Length ||
        !candidate.Blockers.SequenceEqual(existing.Blockers) ||
        !string.Equals(
            candidate.NextSubtaskId,
            existing.NextSubtaskId,
            StringComparison.Ordinal);
}
