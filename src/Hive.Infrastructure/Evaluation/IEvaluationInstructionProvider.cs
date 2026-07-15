using System.Collections.Immutable;
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

    public EvaluationInstruction(
        int rubricVersion,
        string content,
        IEnumerable<EvaluationEnvelopeDimension>? envelopeDimensions = null)
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
        EnvelopeDimensions = SnapshotEnvelopeDimensions(envelopeDimensions);
    }

    public int RubricVersion { get; }

    public string Content { get; }

    /// <summary>
    /// Rubric-declared evaluation-envelope dimensions as opaque descriptors. They allow the
    /// runtime to compose the structured output constraint without compiling any organizational
    /// function semantics; ids and labels are tokens owned by the versioned rubric fixture.
    /// </summary>
    public ImmutableArray<EvaluationEnvelopeDimension> EnvelopeDimensions { get; }

    private static ImmutableArray<EvaluationEnvelopeDimension> SnapshotEnvelopeDimensions(
        IEnumerable<EvaluationEnvelopeDimension>? envelopeDimensions)
    {
        if (envelopeDimensions is null)
        {
            return [];
        }

        var snapshot = envelopeDimensions.ToImmutableArray();
        if (snapshot.Any(dimension => dimension is null))
        {
            throw new ArgumentException(
                "Evaluation envelope dimensions cannot contain null entries.",
                nameof(envelopeDimensions));
        }

        if (snapshot.Select(dimension => dimension.Id).Distinct(StringComparer.Ordinal).Count()
            != snapshot.Length)
        {
            throw new ArgumentException(
                "Evaluation envelope dimension ids must be unique.",
                nameof(envelopeDimensions));
        }

        return snapshot
            .OrderBy(dimension => dimension.Id, StringComparer.Ordinal)
            .ToImmutableArray();
    }
}

/// <summary>
/// One rubric-declared evaluation-envelope dimension seen as pure transport metadata:
/// an opaque id, a cardinality flag, and the closed label vocabulary. No dimension id or
/// label ever gains compiled meaning here.
/// </summary>
public sealed record EvaluationEnvelopeDimension
{
    public EvaluationEnvelopeDimension(string id, bool singleLabel, IEnumerable<string> labels)
    {
        Id = string.IsNullOrWhiteSpace(id)
            ? throw new ArgumentException(
                "Evaluation envelope dimension id is required.",
                nameof(id))
            : id;
        SingleLabel = singleLabel;
        Labels = SnapshotLabels(labels);
    }

    public string Id { get; }

    public bool SingleLabel { get; }

    public ImmutableArray<string> Labels { get; }

    private static ImmutableArray<string> SnapshotLabels(IEnumerable<string> labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        var snapshot = labels.ToImmutableArray();
        if (snapshot.IsEmpty
            || snapshot.Any(string.IsNullOrWhiteSpace)
            || snapshot.Distinct(StringComparer.Ordinal).Count() != snapshot.Length)
        {
            throw new ArgumentException(
                "Evaluation envelope dimension labels must be non-empty, non-blank, and unique.",
                nameof(labels));
        }

        return snapshot;
    }
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
