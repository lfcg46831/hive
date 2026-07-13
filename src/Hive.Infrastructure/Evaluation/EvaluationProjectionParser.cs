using System.Collections.Immutable;
using System.Text.Json;

namespace Hive.Infrastructure.Evaluation;

internal static class EvaluationProjectionParser
{
    public const int ContractVersion = 1;

    public static EvaluationProjection Parse(
        string? content,
        string resultMessageKind,
        EvaluationRubricContract rubric)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(resultMessageKind);
        ArgumentNullException.ThrowIfNull(rubric);

        var envelopeDimensions = ParseEnvelope(content, rubric);
        var dimensions = rubric.Dimensions
            .OrderBy(dimension => dimension.Id, StringComparer.Ordinal)
            .Select(dimension => dimension.Source switch
            {
                "evaluation-envelope" => envelopeDimensions[dimension.Id],
                "result-message-kind" => ProjectResultMessageKind(
                    dimension,
                    resultMessageKind),
                _ => throw new InvalidOperationException(
                    $"Unsupported evaluation dimension source '{dimension.Source}'."),
            })
            .ToImmutableArray();

        return new EvaluationProjection(ContractVersion, rubric.RubricVersion, dimensions);
    }

    private static IReadOnlyDictionary<string, EvaluationDimensionProjection> ParseEnvelope(
        string? content,
        EvaluationRubricContract rubric)
    {
        var dimensions = rubric.Dimensions
            .Where(dimension => dimension.Source == "evaluation-envelope")
            .ToArray();
        var payloads = (content ?? string.Empty)
            .Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.StartsWith(
                EvaluationInstruction.EnvelopeMarker,
                StringComparison.Ordinal))
            .Select(line => line[EvaluationInstruction.EnvelopeMarker.Length..].Trim())
            .ToArray();

        if (payloads.Length == 0)
        {
            return WithStatus(dimensions, EvaluationDimensionStatus.Missing);
        }

        if (payloads.Length != 1 || payloads[0].Length == 0)
        {
            return WithStatus(dimensions, EvaluationDimensionStatus.Invalid);
        }

        try
        {
            using var document = JsonDocument.Parse(payloads[0]);
            var root = document.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            var dimensionContainers = rootProperties
                .Where(property => property.NameEquals("dimensions"))
                .ToArray();
            if (root.ValueKind != JsonValueKind.Object
                || rootProperties.Any(property => !property.NameEquals("dimensions"))
                || dimensionContainers.Length != 1
                || dimensionContainers[0].Value.ValueKind != JsonValueKind.Object)
            {
                return WithStatus(dimensions, EvaluationDimensionStatus.Invalid);
            }

            var projected = new Dictionary<string, EvaluationDimensionProjection>(
                StringComparer.Ordinal);
            var properties = dimensionContainers[0].Value
                .EnumerateObject()
                .ToArray();
            foreach (var dimension in dimensions)
            {
                var matches = properties
                    .Where(property => property.NameEquals(dimension.Id))
                    .ToArray();
                projected.Add(
                    dimension.Id,
                    matches.Length switch
                    {
                        0 => EvaluationDimensionProjection.Missing(dimension.Id),
                        1 => ProjectEnvelopeDimension(dimension, matches[0].Value),
                        _ => EvaluationDimensionProjection.Invalid(dimension.Id),
                    });
            }

            return projected;
        }
        catch (JsonException)
        {
            return WithStatus(dimensions, EvaluationDimensionStatus.Invalid);
        }
    }

    private static EvaluationDimensionProjection ProjectEnvelopeDimension(
        EvaluationDimensionContract dimension,
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return EvaluationDimensionProjection.Invalid(dimension.Id);
        }

        var labels = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var label = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            if (label is null
                || !dimension.Labels.Contains(label, StringComparer.Ordinal))
            {
                return EvaluationDimensionProjection.Invalid(dimension.Id);
            }

            labels.Add(label);
        }

        if (dimension.ValueKind == "single-label" && labels.Count != 1)
        {
            return EvaluationDimensionProjection.Invalid(dimension.Id);
        }

        var canonicalLabels = dimension.ValueKind == "label-set"
            ? labels.Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToImmutableArray()
            : labels.ToImmutableArray();
        return EvaluationDimensionProjection.Valid(dimension.Id, canonicalLabels);
    }

    private static EvaluationDimensionProjection ProjectResultMessageKind(
        EvaluationDimensionContract dimension,
        string resultMessageKind) =>
        dimension.SourceMapping.TryGetValue(resultMessageKind, out var label)
            ? EvaluationDimensionProjection.Valid(dimension.Id, [label])
            : EvaluationDimensionProjection.Invalid(dimension.Id);

    private static IReadOnlyDictionary<string, EvaluationDimensionProjection> WithStatus(
        IEnumerable<EvaluationDimensionContract> dimensions,
        EvaluationDimensionStatus status) => dimensions.ToDictionary(
            dimension => dimension.Id,
            dimension => status == EvaluationDimensionStatus.Missing
                ? EvaluationDimensionProjection.Missing(dimension.Id)
                : EvaluationDimensionProjection.Invalid(dimension.Id),
            StringComparer.Ordinal);
}

internal enum EvaluationDimensionStatus
{
    Valid,
    Missing,
    Invalid,
}

internal sealed record EvaluationProjection(
    int ContractVersion,
    int RubricVersion,
    ImmutableArray<EvaluationDimensionProjection> Dimensions);

internal sealed record EvaluationDimensionProjection(
    string DimensionId,
    EvaluationDimensionStatus Status,
    ImmutableArray<string> Labels)
{
    public static EvaluationDimensionProjection Valid(
        string dimensionId,
        ImmutableArray<string> labels) =>
        new(dimensionId, EvaluationDimensionStatus.Valid, labels);

    public static EvaluationDimensionProjection Missing(string dimensionId) =>
        new(dimensionId, EvaluationDimensionStatus.Missing, []);

    public static EvaluationDimensionProjection Invalid(string dimensionId) =>
        new(dimensionId, EvaluationDimensionStatus.Invalid, []);
}
