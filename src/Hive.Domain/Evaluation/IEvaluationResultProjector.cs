using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Evaluation;

/// <summary>
/// Optional evaluation-only seam that derives safe labels from a canonical result message
/// before its free-text payload is redacted from durable audit projections.
/// </summary>
public interface IEvaluationResultProjector
{
    ValueTask ProjectAsync(
        DirectiveId directiveId,
        OrgMessage resultMessage,
        CancellationToken cancellationToken = default);
}

public sealed class NoopEvaluationResultProjector : IEvaluationResultProjector
{
    public static NoopEvaluationResultProjector Instance { get; } = new();

    private NoopEvaluationResultProjector()
    {
    }

    public ValueTask ProjectAsync(
        DirectiveId directiveId,
        OrgMessage resultMessage,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directiveId);
        ArgumentNullException.ThrowIfNull(resultMessage);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.CompletedTask;
    }
}
