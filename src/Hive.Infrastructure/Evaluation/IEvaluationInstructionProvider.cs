using Hive.Domain.Identity;

namespace Hive.Infrastructure.Evaluation;

/// <summary>
/// Resolves the optional, ephemeral evaluation instruction for one organization/position scope.
/// The instruction is runtime configuration and must not be persisted as organizational identity.
/// </summary>
public interface IEvaluationInstructionProvider
{
    EvaluationInstruction? Resolve(OrganizationId organizationId, PositionId positionId);
}

public sealed record EvaluationInstruction
{
    public const string EnvelopeMarker = "hive-evaluation-v1:";

    public EvaluationInstruction(int rubricVersion, string content)
    {
        if (rubricVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(rubricVersion),
                rubricVersion,
                "Evaluation rubric version must be greater than zero.");
        }

        RubricVersion = rubricVersion;
        Content = string.IsNullOrWhiteSpace(content)
            ? throw new ArgumentException(
                "Evaluation instruction content is required.",
                nameof(content))
            : content.Trim();
    }

    public int RubricVersion { get; }

    public string Content { get; }
}

public sealed class NoopEvaluationInstructionProvider : IEvaluationInstructionProvider
{
    public static NoopEvaluationInstructionProvider Instance { get; } = new();

    private NoopEvaluationInstructionProvider()
    {
    }

    public EvaluationInstruction? Resolve(
        OrganizationId organizationId,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        return null;
    }
}
