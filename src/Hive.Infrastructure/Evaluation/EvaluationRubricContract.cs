using System.Collections.Immutable;
using System.Text;
using System.Text.Json;

namespace Hive.Infrastructure.Evaluation;

internal sealed record EvaluationRubricContract(
    int RubricVersion,
    ImmutableArray<EvaluationDimensionContract> Dimensions)
{
    private static readonly HashSet<string> SupportedSources =
        ["evaluation-envelope", "result-message-kind"];

    private static readonly HashSet<string> SupportedValueKinds =
        ["single-label", "label-set"];

    private static readonly HashSet<string> SupportedScorers =
        ["ordinal-distance", "set-f1", "exact-match"];

    public static EvaluationRubricContract Load(string path, int expectedVersion)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Evaluation rubric path is required.", nameof(path));
        }

        if (expectedVersion <= 0)
        {
            throw new InvalidDataException(
                "An enabled evaluation profile must declare a positive RubricVersion.");
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new InvalidDataException("Evaluation rubric root must be an object.");
            }

            var rubricVersion = root.GetProperty("rubric_version").GetInt32();
            if (rubricVersion != expectedVersion)
            {
                throw new InvalidDataException(
                    $"Evaluation rubric version '{rubricVersion}' does not match configured version '{expectedVersion}'.");
            }

            var dimensionsElement = root.GetProperty("dimensions");
            if (dimensionsElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Evaluation rubric dimensions must be an array.");
            }

            var dimensions = dimensionsElement
                .EnumerateArray()
                .Select(ReadDimension)
                .ToImmutableArray();
            if (dimensions.IsEmpty)
            {
                throw new InvalidDataException("Evaluation rubric must declare at least one dimension.");
            }

            var duplicate = dimensions
                .GroupBy(dimension => dimension.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            if (duplicate is not null)
            {
                throw new InvalidDataException(
                    $"Evaluation rubric dimension '{duplicate.Key}' is duplicated.");
            }

            if (dimensions.Sum(dimension => dimension.Weight) != 1m)
            {
                throw new InvalidDataException("Evaluation rubric dimension weights must sum to exactly 1.");
            }

            if (!dimensions.Any(dimension => dimension.Source == "evaluation-envelope"))
            {
                throw new InvalidDataException(
                    "Evaluation rubric must declare at least one evaluation-envelope dimension.");
            }

            return new EvaluationRubricContract(rubricVersion, dimensions);
        }
        catch (Exception exception)
            when (exception is JsonException
                or KeyNotFoundException
                or InvalidOperationException
                or FormatException
                or OverflowException)
        {
            throw new InvalidDataException("Evaluation rubric contract is malformed.", exception);
        }
    }

    public EvaluationInstruction BuildInstruction()
    {
        var envelopeDimensions = Dimensions
            .Where(dimension => dimension.Source == "evaluation-envelope")
            .OrderBy(dimension => dimension.Id, StringComparer.Ordinal)
            .ToArray();
        var exampleDimensions = envelopeDimensions.ToDictionary(
            dimension => dimension.Id,
            dimension => dimension.ValueKind == "single-label"
                ? new[] { dimension.Labels[0] }
                : Array.Empty<string>(),
            StringComparer.Ordinal);
        var compactExample = JsonSerializer.Serialize(
            new Dictionary<string, object?>
            {
                ["dimensions"] = exampleDimensions,
            });

        var builder = new StringBuilder();
        builder.AppendLine("For every Report, put exactly one standalone evaluation envelope line in report.body.");
        builder.AppendLine("For every Escalation, put exactly one standalone evaluation envelope line in escalation.context.");
        builder.Append("Use this compact envelope shape: ")
            .Append(EvaluationInstruction.EnvelopeMarker)
            .AppendLine(compactExample);
        builder.AppendLine("The dimensions object must contain exactly the evaluation-envelope dimensions declared below:");

        foreach (var dimension in envelopeDimensions)
        {
            builder.Append("- ")
                .Append(JsonSerializer.Serialize(dimension.Id))
                .Append(" (")
                .Append(dimension.ValueKind)
                .Append("): ")
                .Append(dimension.ValueKind == "single-label"
                    ? "exactly one label from "
                    : "zero or more labels from ")
                .Append(JsonSerializer.Serialize(dimension.Labels))
                .AppendLine(".");
        }

        builder.AppendLine("Collapse duplicate set labels and order emitted dimension ids and labels lexically.");
        builder.AppendLine("Do not include dimensions derived from HIVE result facts in the envelope.");
        builder.Append("Do not invent aliases, move the line to another field, or emit it more than once.");

        return new EvaluationInstruction(RubricVersion, builder.ToString());
    }

    private static EvaluationDimensionContract ReadDimension(JsonElement dimension)
    {
        if (dimension.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException("Evaluation rubric dimensions must be objects.");
        }

        var id = RequiredString(dimension, "id");
        var source = RequiredString(dimension, "source");
        var valueKind = RequiredString(dimension, "value_kind");
        var scorer = RequiredString(dimension, "scorer");
        var weight = dimension.GetProperty("weight").GetDecimal();

        if (!SupportedSources.Contains(source))
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' declares unknown source '{source}'.");
        }

        if (!SupportedValueKinds.Contains(valueKind))
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' declares unknown value kind '{valueKind}'.");
        }

        if (!SupportedScorers.Contains(scorer))
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' declares unknown scorer '{scorer}'.");
        }

        if (weight <= 0m || weight > 1m)
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' has an invalid weight.");
        }

        var labelProperty = scorer == "ordinal-distance" ? "ordered_labels" : "allowed_labels";
        var labels = RequiredLabels(dimension, labelProperty, id);
        var compatible = scorer switch
        {
            "ordinal-distance" => valueKind == "single-label",
            "set-f1" => valueKind == "label-set",
            "exact-match" => valueKind == "single-label",
            _ => false,
        };
        if (!compatible)
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' has incompatible value kind '{valueKind}' and scorer '{scorer}'.");
        }

        return new EvaluationDimensionContract(id, source, valueKind, scorer, weight, labels);
    }

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Evaluation rubric property '{property}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static ImmutableArray<string> RequiredLabels(
        JsonElement dimension,
        string property,
        string dimensionId)
    {
        var element = dimension.GetProperty(property);
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{dimensionId}' property '{property}' must be an array.");
        }

        var labels = element
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(item.GetString())
                    ? item.GetString()!
                    : throw new InvalidDataException(
                        $"Evaluation rubric dimension '{dimensionId}' contains an invalid label."))
            .ToImmutableArray();
        if (labels.IsEmpty
            || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{dimensionId}' must declare a non-empty, unique label vocabulary.");
        }

        return labels;
    }
}

internal sealed record EvaluationDimensionContract(
    string Id,
    string Source,
    string ValueKind,
    string Scorer,
    decimal Weight,
    ImmutableArray<string> Labels);
