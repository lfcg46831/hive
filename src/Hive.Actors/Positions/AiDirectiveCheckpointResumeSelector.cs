using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Actors.Positions;

/// <summary>
/// Selects one resumable checkpoint without semantic matching. Only exact organization, position,
/// thread, directive ancestry, or position-task correlation can make a checkpoint eligible.
/// </summary>
internal static class AiDirectiveCheckpointResumeSelector
{
    public static DirectiveCheckpoint? Select(
        AiDirectiveExecutionContext context,
        IEnumerable<DirectiveCheckpoint> checkpoints)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(checkpoints);

        var ancestry = BuildAncestry(context);
        var taskId = context.TaskState.Task?.TaskId;
        return checkpoints
            .Where(checkpoint => IsGroundedCandidate(context, checkpoint))
            .Select(checkpoint => new
            {
                Checkpoint = checkpoint,
                Rank = Rank(context, checkpoint, ancestry, taskId),
            })
            .Where(candidate => candidate.Rank is not null)
            .OrderBy(candidate => candidate.Rank!.Value.Kind)
            .ThenBy(candidate => candidate.Rank!.Value.Depth)
            .ThenByDescending(candidate => candidate.Checkpoint.Revision)
            .ThenBy(candidate => candidate.Checkpoint.Correlation.DirectiveId.Value)
            .Select(candidate => candidate.Checkpoint)
            .FirstOrDefault();
    }

    private static bool IsGroundedCandidate(
        AiDirectiveExecutionContext context,
        DirectiveCheckpoint checkpoint) =>
        checkpoint.Correlation.OrganizationId == context.OrganizationId &&
        checkpoint.Correlation.PositionId == context.PositionId &&
        checkpoint.Correlation.ThreadId == context.Directive.ThreadId &&
        checkpoint.ContractVersion == DirectiveCheckpointContractVersions.V1 &&
        checkpoint.Plan.ContractVersion == DirectiveCheckpointContractVersions.V1 &&
        DirectiveCheckpointContextProjector.TryProject(checkpoint, out _);

    private static (int Kind, int Depth)? Rank(
        AiDirectiveExecutionContext context,
        DirectiveCheckpoint checkpoint,
        IReadOnlyDictionary<DirectiveId, int> ancestry,
        PositionTaskId? taskId)
    {
        if (checkpoint.Correlation.DirectiveId == context.Directive.DirectiveId)
        {
            return (0, 0);
        }

        if (ancestry.TryGetValue(checkpoint.Correlation.DirectiveId, out var depth))
        {
            return (1, depth);
        }

        return taskId is not null && checkpoint.Correlation.PositionTaskId == taskId
            ? (2, 0)
            : null;
    }

    private static Dictionary<DirectiveId, int> BuildAncestry(
        AiDirectiveExecutionContext context)
    {
        var directives = context.MaterializedHistory
            .OfType<OrgDirective>()
            .Where(directive =>
                directive.OrganizationId == context.OrganizationId &&
                directive.Thread == context.Directive.ThreadId)
            .GroupBy(directive => directive.DirectiveId)
            .ToDictionary(group => group.Key, group => group.First());
        var ancestry = new Dictionary<DirectiveId, int>();
        var parent = context.Directive.ParentDirectiveId;
        var depth = 1;
        while (parent is not null && ancestry.TryAdd(parent, depth))
        {
            parent = directives.GetValueOrDefault(parent)?.ParentDirectiveId;
            depth++;
        }

        return ancestry;
    }
}
