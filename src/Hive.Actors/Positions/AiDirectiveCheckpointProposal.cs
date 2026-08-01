using System.Collections.Immutable;
using Hive.Domain.Directives;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

/// <summary>
/// Provider-authored semantic portion of a checkpoint. Correlation and revision remain runtime
/// facts and are stamped only after the proposal has passed strict interpretation.
/// </summary>
internal sealed record AiDirectiveCheckpointProposal
{
    public AiDirectiveCheckpointProposal(
        int contractVersion,
        DirectiveCheckpointPlan plan,
        IEnumerable<CompletedDirectiveCheckpointSubtask> completedSubtasks,
        IEnumerable<OutcomeBlocker>? blockers,
        string nextSubtaskId)
    {
        if (contractVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                "Checkpoint proposal contract version must be positive.");
        }

        ArgumentNullException.ThrowIfNull(completedSubtasks);
        var completed = completedSubtasks.ToImmutableArray();
        if (completed.IsEmpty || completed.Any(subtask => subtask is null))
        {
            throw new ArgumentException(
                "A checkpoint proposal requires at least one completed subtask.",
                nameof(completedSubtasks));
        }

        ContractVersion = contractVersion;
        Plan = plan ?? throw new ArgumentNullException(nameof(plan));
        CompletedSubtasks = completed;
        Blockers = (blockers ?? []).ToImmutableArray();
        NextSubtaskId = nextSubtaskId;
    }

    public int ContractVersion { get; }

    public DirectiveCheckpointPlan Plan { get; }

    public ImmutableArray<CompletedDirectiveCheckpointSubtask> CompletedSubtasks { get; }

    public ImmutableArray<OutcomeBlocker> Blockers { get; }

    public string NextSubtaskId { get; }

    public DirectiveCheckpoint Materialize(
        AiDirectiveExecutionContext context,
        int revision)
    {
        ArgumentNullException.ThrowIfNull(context);
        var correlation = new DirectiveCheckpointCorrelation(
            context.OrganizationId,
            context.PositionId,
            context.Directive.ThreadId,
            context.Directive.DirectiveId,
            context.Directive.ParentDirectiveId,
            context.TaskState.Task?.TaskId);
        return new DirectiveCheckpoint(
            ContractVersion,
            revision,
            Plan,
            correlation,
            CompletedSubtasks,
            Blockers,
            NextSubtaskId);
    }
}
