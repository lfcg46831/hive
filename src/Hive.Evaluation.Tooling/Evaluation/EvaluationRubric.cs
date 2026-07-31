using System.Text;
using System.Text.Json;

namespace Hive.Evaluation.Tooling.Evaluation;

public sealed class EvaluationRubric
{
    private const int SupportedProjectionVersion = 1;
    private const string EnvelopeMarker = "hive-evaluation-v1:";
    private static readonly IReadOnlyDictionary<ScorerKey, ScorerFactory> ScorerRegistry =
        new Dictionary<ScorerKey, ScorerFactory>
        {
            [new("ordinal-distance", 1)] = OrdinalDistanceScorer.Create,
            [new("set-f1", 1)] = SetF1Scorer.Create,
            [new("exact-match", 1)] = ExactMatchScorer.Create,
        };

    private readonly IReadOnlyList<DimensionRuntime> _dimensions;
    private readonly EvaluationAggregationDescriptor _aggregation;

    private EvaluationRubric(
        int rubricVersion,
        int corpusVersion,
        IReadOnlyList<DimensionRuntime> dimensions,
        EvaluationAggregationDescriptor aggregation)
    {
        RubricVersion = rubricVersion;
        CorpusVersion = corpusVersion;
        _dimensions = dimensions;
        _aggregation = aggregation;
        Dimensions = dimensions.Select(item => item.Descriptor).ToArray();
    }

    public int RubricVersion { get; }

    public int CorpusVersion { get; }

    public IReadOnlyList<EvaluationDimensionDescriptor> Dimensions { get; }

    internal EvaluationDecisionAnalysisConfiguration DecisionAnalysis()
    {
        var candidates = _dimensions
            .Where(item => item.Descriptor.Source == "result-message-kind")
            .ToArray();
        if (candidates.Length != 1
            || !candidates[0].SourceMapping.TryGetValue("report", out var negativeLabel)
            || !candidates[0].SourceMapping.TryGetValue("escalation", out var positiveLabel)
            || string.Equals(negativeLabel, positiveLabel, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Evaluation rubric must define one report/escalation decision dimension for run analysis.");
        }

        return new EvaluationDecisionAnalysisConfiguration(
            candidates[0].Descriptor.Id,
            negativeLabel,
            positiveLabel);
    }

    public string BuildObservationInstruction()
    {
        var envelopeDimensions = _dimensions
            .Where(item => item.Descriptor.Source == "evaluation-envelope")
            .OrderBy(item => item.Descriptor.Id, StringComparer.Ordinal)
            .ToArray();
        var example = envelopeDimensions.ToDictionary(
            item => item.Descriptor.Id,
            item => item.Descriptor.ValueKind == "single-label"
                ? new[] { item.Descriptor.Labels[0] }
                : Array.Empty<string>(),
            StringComparer.Ordinal);
        var compactExample = JsonSerializer.Serialize(
            new Dictionary<string, object?> { ["dimensions"] = example });

        var builder = new StringBuilder();
        builder.AppendLine("Experimental observation requirement (tooling-owned):");
        builder.AppendLine(
            "For a Report, end report.body with exactly one observation line; for an Escalation, end escalation.context with exactly one observation line.");
        builder.Append("Use this exact marker and compact shape: ")
            .Append(EnvelopeMarker)
            .AppendLine(compactExample);
        foreach (var item in envelopeDimensions)
        {
            builder.Append("- ")
                .Append(JsonSerializer.Serialize(item.Descriptor.Id))
                .Append(" (")
                .Append(item.Descriptor.ValueKind)
                .Append("): ")
                .Append(item.Descriptor.ValueKind == "single-label"
                    ? "exactly one label from "
                    : "zero or more labels from ")
                .Append(JsonSerializer.Serialize(item.Descriptor.Labels))
                .AppendLine(".");
        }

        builder.Append(
            "Do not add rubric ids or labels anywhere except that final observation line.");
        return builder.ToString();
    }

    public EvaluationPrediction? ProjectResult(
        string messageType,
        string canonicalContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(messageType);
        ArgumentException.ThrowIfNullOrWhiteSpace(canonicalContent);

        string? payload;
        try
        {
            using var document = JsonDocument.Parse(canonicalContent);
            var property = string.Equals(messageType, "Report", StringComparison.Ordinal)
                ? "Body"
                : string.Equals(messageType, "Escalation", StringComparison.Ordinal)
                    ? "Context"
                    : null;
            payload = property is null
                ? null
                : TryGetString(document.RootElement, property);
        }
        catch (JsonException)
        {
            return null;
        }

        var envelope = ProjectEnvelope(payload);
        var resultKind = messageType.ToLowerInvariant();
        var dimensions = _dimensions
            .Select(item => item.Descriptor.Source switch
            {
                "evaluation-envelope" => envelope[item.Descriptor.Id],
                "result-message-kind" => item.SourceMapping.TryGetValue(
                        resultKind,
                        out var label)
                    ? Valid(item.Descriptor.Id, [label])
                    : Invalid(item.Descriptor.Id, "unexpected-shape"),
                _ => Invalid(item.Descriptor.Id, "unexpected-shape"),
            })
            .ToArray();
        return new EvaluationPrediction(
            SupportedProjectionVersion,
            RubricVersion,
            dimensions);
    }

    public static EvaluationRubric Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Rubric path is required.", nameof(path));
        }

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            var aggregation = ReadAggregation(root.GetProperty("aggregation"));
            var dimensions = root.GetProperty("dimensions")
                .EnumerateArray()
                .Select(ReadDimension)
                .OrderBy(item => item.Descriptor.Id, StringComparer.Ordinal)
                .ToArray();

            var duplicate = dimensions
                .GroupBy(item => item.Descriptor.Id, StringComparer.Ordinal)
                .FirstOrDefault(group => group.Count() > 1);
            var totalWeight = dimensions.Sum(item => item.Descriptor.Weight);
            var scoreScale = root.GetProperty("score_scale");
            var rubricVersion = root.GetProperty("rubric_version").GetInt32();
            var corpusVersion = root.GetProperty("applies_to")
                .GetProperty("corpus_version")
                .GetInt32();

            if (root.ValueKind != JsonValueKind.Object
                || RequiredString(root, "fixture_kind") != "evaluation-example"
                || rubricVersion != 1
                || corpusVersion != 1
                || dimensions.Length == 0
                || duplicate is not null
                || totalWeight != 1m
                || scoreScale.GetProperty("minimum").GetDouble() != 0d
                || scoreScale.GetProperty("maximum").GetDouble() != 1d
                || !scoreScale.GetProperty("higher_is_better").GetBoolean())
            {
                throw new InvalidDataException(
                    "Evaluation rubric version, dimensions, weights, or score scale are invalid.");
            }

            return new EvaluationRubric(
                rubricVersion,
                corpusVersion,
                dimensions,
                aggregation);
        }
        catch (InvalidDataException)
        {
            throw;
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

    public void ValidateCorpus(EvaluationCorpus corpus)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        if (corpus.CorpusVersion != CorpusVersion)
        {
            throw new InvalidDataException(
                $"Rubric corpus version '{CorpusVersion}' does not match corpus version '{corpus.CorpusVersion}'.");
        }

        foreach (var item in corpus.Cases)
        {
            try
            {
                ValidateReference(item.HumanReference);
            }
            catch (InvalidDataException exception)
            {
                throw new InvalidDataException(
                    $"Evaluation case '{item.CaseId}' is outside the rubric vocabulary: {exception.Message}",
                    exception);
            }
        }
    }

    public EvaluationPrediction? NormalizePrediction(EvaluationPrediction? prediction)
    {
        if (prediction is null)
        {
            return null;
        }

        var versionCompatible = prediction.ProjectionVersion == SupportedProjectionVersion
            && prediction.RubricVersion == RubricVersion;
        var groups = prediction.Dimensions
            .GroupBy(item => item.DimensionId, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.ToArray(), StringComparer.Ordinal);
        var dimensions = _dimensions
            .Select(dimension => versionCompatible
                ? NormalizeDimension(
                    dimension,
                    groups.TryGetValue(dimension.Descriptor.Id, out var matches)
                        ? matches
                        : [])
                : Invalid(dimension.Descriptor.Id))
            .ToArray();

        return prediction with { Dimensions = dimensions };
    }

    public EvaluationCaseScoring Score(
        IReadOnlyDictionary<string, IReadOnlyList<string>> reference,
        EvaluationPrediction? prediction)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateReference(reference);

        var failures = new HashSet<string>(StringComparer.Ordinal);
        if (prediction is not null
            && prediction.ProjectionVersion != SupportedProjectionVersion)
        {
            failures.Add("projection-version-unsupported");
        }

        if (prediction is not null && prediction.RubricVersion != RubricVersion)
        {
            failures.Add("projection-rubric-version-mismatch");
        }

        var normalized = NormalizePrediction(prediction);
        var predictions = normalized?.Dimensions.ToDictionary(
            item => item.DimensionId,
            StringComparer.Ordinal);
        var scores = new List<EvaluationDimensionScoring>(_dimensions.Count);
        foreach (var dimension in _dimensions)
        {
            var projected = predictions is not null
                && predictions.TryGetValue(dimension.Descriptor.Id, out var value)
                    ? value
                    : Missing(dimension.Descriptor.Id);
            if (projected.Status == EvaluationDimensionStatuses.Missing)
            {
                failures.Add("projection-missing");
            }
            else if (projected.Status == EvaluationDimensionStatuses.Invalid)
            {
                failures.Add("projection-invalid");
            }

            var score = projected.Status == EvaluationDimensionStatuses.Valid
                ? dimension.Scorer.Score(projected.Labels, reference[dimension.Descriptor.Id])
                : 0d;
            scores.Add(new EvaluationDimensionScoring(
                dimension.Descriptor.Id,
                projected.Status,
                projected.Labels,
                score));
        }

        var caseScore = NormalizeScoreBoundary(_aggregation.CaseScore switch
        {
            "weighted-arithmetic-mean" => scores.Zip(
                    _dimensions,
                    (score, dimension) => (double)dimension.Descriptor.Weight * score.Score)
                .Sum(),
            _ => throw new InvalidOperationException(
                $"Unsupported case aggregation '{_aggregation.CaseScore}'."),
        });

        return new EvaluationCaseScoring(
            failures.Count == 0 ? "scored" : "failed",
            failures.OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            scores,
            caseScore);
    }

    public double ScoreCorpus(IReadOnlyCollection<EvaluationCaseScoring> cases)
    {
        ArgumentNullException.ThrowIfNull(cases);
        if (cases.Count == 0)
        {
            throw new ArgumentException("At least one scored case is required.", nameof(cases));
        }

        return _aggregation.CorpusScore switch
        {
            "unweighted-macro-mean-of-case-scores" => cases.Average(item => item.CaseScore),
            _ => throw new InvalidOperationException(
                $"Unsupported corpus aggregation '{_aggregation.CorpusScore}'."),
        };
    }

    private static DimensionRuntime ReadDimension(JsonElement element)
    {
        var id = RequiredString(element, "id");
        var source = RequiredString(element, "source");
        var valueKind = RequiredString(element, "value_kind");
        var scorerId = RequiredString(element, "scorer");
        var scorerVersion = element.GetProperty("scorer_version").GetInt32();
        var weight = element.GetProperty("weight").GetDecimal();
        var key = new ScorerKey(scorerId, scorerVersion);

        if (source is not ("evaluation-envelope" or "result-message-kind"))
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' declares unsupported source '{source}'.");
        }

        if (weight <= 0m || weight > 1m)
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' has an invalid weight.");
        }

        if (!ScorerRegistry.TryGetValue(key, out var factory))
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{id}' declares unsupported scorer '{scorerId}' version '{scorerVersion}'.");
        }

        var scorer = factory(element, valueKind);
        var sourceMapping = source == "result-message-kind"
            ? ReadSourceMapping(element, scorer.Labels, id)
            : new Dictionary<string, string>(StringComparer.Ordinal);
        return new DimensionRuntime(
            new EvaluationDimensionDescriptor(
                id,
                source,
                valueKind,
                weight,
                new EvaluationScorerDescriptor(scorerId, scorerVersion),
                scorer.Labels),
            scorer,
            sourceMapping);
    }

    private IReadOnlyDictionary<string, EvaluationDimensionPrediction> ProjectEnvelope(
        string? payload)
    {
        var dimensions = _dimensions
            .Where(item => item.Descriptor.Source == "evaluation-envelope")
            .ToArray();
        if (payload is null)
        {
            return WithEnvelopeStatus(dimensions, EvaluationDimensionStatuses.Missing, "envelope-missing");
        }

        var first = payload.IndexOf(EnvelopeMarker, StringComparison.Ordinal);
        if (first < 0)
        {
            return WithEnvelopeStatus(dimensions, EvaluationDimensionStatuses.Missing, "envelope-missing");
        }

        if (payload.IndexOf(
                EnvelopeMarker,
                first + EnvelopeMarker.Length,
                StringComparison.Ordinal) >= 0)
        {
            return WithEnvelopeStatus(dimensions, EvaluationDimensionStatuses.Invalid, "envelope-duplicated");
        }

        try
        {
            var bytes = Encoding.UTF8.GetBytes(payload[(first + EnvelopeMarker.Length)..]);
            var reader = new Utf8JsonReader(bytes);
            using var document = JsonDocument.ParseValue(ref reader);
            var root = document.RootElement;
            var rootProperties = root.ValueKind == JsonValueKind.Object
                ? root.EnumerateObject().ToArray()
                : [];
            var containers = rootProperties
                .Where(property => property.NameEquals("dimensions"))
                .ToArray();
            if (root.ValueKind != JsonValueKind.Object ||
                rootProperties.Any(property => !property.NameEquals("dimensions")) ||
                containers.Length != 1 ||
                containers[0].Value.ValueKind != JsonValueKind.Object)
            {
                return WithEnvelopeStatus(
                    dimensions,
                    EvaluationDimensionStatuses.Invalid,
                    "unexpected-shape");
            }

            var properties = containers[0].Value.EnumerateObject().ToArray();
            return dimensions.ToDictionary(
                item => item.Descriptor.Id,
                item =>
                {
                    var matches = properties
                        .Where(property => property.NameEquals(item.Descriptor.Id))
                        .ToArray();
                    return matches.Length switch
                    {
                        0 => Missing(item.Descriptor.Id, "envelope-missing"),
                        1 => ProjectDimension(item.Descriptor, matches[0].Value),
                        _ => Invalid(item.Descriptor.Id, "unexpected-shape"),
                    };
                },
                StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return WithEnvelopeStatus(
                dimensions,
                EvaluationDimensionStatuses.Invalid,
                "payload-not-json");
        }
    }

    private static EvaluationDimensionPrediction ProjectDimension(
        EvaluationDimensionDescriptor dimension,
        JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array)
        {
            return Invalid(dimension.Id, "unexpected-shape");
        }

        var labels = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            var label = item.ValueKind == JsonValueKind.String
                ? item.GetString()
                : null;
            if (label is null)
            {
                return Invalid(dimension.Id, "unexpected-shape");
            }

            if (!dimension.Labels.Contains(label, StringComparer.Ordinal))
            {
                return Invalid(dimension.Id, "unknown-label");
            }

            labels.Add(label);
        }

        if (dimension.ValueKind == "single-label" && labels.Count != 1)
        {
            return Invalid(dimension.Id, "cardinality-violation");
        }

        var canonical = dimension.ValueKind == "label-set"
            ? labels.Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray()
            : labels.ToArray();
        return Valid(dimension.Id, canonical);
    }

    private static IReadOnlyDictionary<string, EvaluationDimensionPrediction> WithEnvelopeStatus(
        IEnumerable<DimensionRuntime> dimensions,
        string status,
        string diagnosticCode) =>
        dimensions.ToDictionary(
            item => item.Descriptor.Id,
            item => status == EvaluationDimensionStatuses.Missing
                ? Missing(item.Descriptor.Id, diagnosticCode)
                : Invalid(item.Descriptor.Id, diagnosticCode),
            StringComparer.Ordinal);

    private static IReadOnlyDictionary<string, string> ReadSourceMapping(
        JsonElement element,
        IReadOnlyList<string> labels,
        string dimensionId)
    {
        if (!element.TryGetProperty("source_mapping", out var mapping) ||
            mapping.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException(
                $"Evaluation rubric dimension '{dimensionId}' requires source_mapping.");
        }

        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in mapping.EnumerateObject())
        {
            var label = property.Value.ValueKind == JsonValueKind.String
                ? property.Value.GetString()
                : null;
            if (label is null ||
                !labels.Contains(label, StringComparer.Ordinal) ||
                !result.TryAdd(property.Name, label))
            {
                throw new InvalidDataException(
                    $"Evaluation rubric dimension '{dimensionId}' has an invalid source_mapping.");
            }
        }

        return result;
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }

        return null;
    }

    private static EvaluationAggregationDescriptor ReadAggregation(JsonElement element)
    {
        var aggregation = new EvaluationAggregationDescriptor(
            RequiredString(element, "case_score"),
            RequiredString(element, "corpus_score"),
            RequiredString(element, "rounding"));
        if (aggregation.CaseScore != "weighted-arithmetic-mean"
            || aggregation.CorpusScore != "unweighted-macro-mean-of-case-scores"
            || aggregation.Rounding != "none-until-presentation")
        {
            throw new InvalidDataException("Evaluation rubric has unsupported aggregation rules.");
        }

        return aggregation;
    }

    private void ValidateReference(
        IReadOnlyDictionary<string, IReadOnlyList<string>> reference)
    {
        var declaredIds = _dimensions
            .Select(item => item.Descriptor.Id)
            .ToHashSet(StringComparer.Ordinal);
        if (reference.Count != declaredIds.Count
            || reference.Keys.Any(id => !declaredIds.Contains(id)))
        {
            throw new InvalidDataException(
                "Human reference dimensions must exactly match the rubric dimensions.");
        }

        foreach (var dimension in _dimensions)
        {
            var labels = reference[dimension.Descriptor.Id];
            if (labels is null
                || labels.Distinct(StringComparer.Ordinal).Count() != labels.Count
                || !dimension.Scorer.Accepts(labels))
            {
                throw new InvalidDataException(
                    $"Human reference dimension '{dimension.Descriptor.Id}' has invalid labels or cardinality.");
            }
        }
    }

    private static EvaluationDimensionPrediction NormalizeDimension(
        DimensionRuntime dimension,
        IReadOnlyList<EvaluationDimensionPrediction> matches)
    {
        if (matches.Count == 0)
        {
            return Missing(dimension.Descriptor.Id);
        }

        if (matches.Count != 1)
        {
            return Invalid(dimension.Descriptor.Id);
        }

        var match = matches[0];
        if (match.Status == EvaluationDimensionStatuses.Missing)
        {
            return match.Labels.Count == 0
                ? Missing(dimension.Descriptor.Id)
                : Invalid(dimension.Descriptor.Id);
        }

        if (match.Status != EvaluationDimensionStatuses.Valid)
        {
            return Invalid(dimension.Descriptor.Id);
        }

        var labels = dimension.Descriptor.ValueKind == "label-set"
            ? match.Labels.Distinct(StringComparer.Ordinal)
                .OrderBy(label => label, StringComparer.Ordinal)
                .ToArray()
            : match.Labels.ToArray();
        return dimension.Scorer.Accepts(labels)
            ? new EvaluationDimensionPrediction(
                dimension.Descriptor.Id,
                EvaluationDimensionStatuses.Valid,
                labels)
            : Invalid(dimension.Descriptor.Id);
    }

    private static EvaluationDimensionPrediction Missing(string dimensionId) =>
        new(dimensionId, EvaluationDimensionStatuses.Missing, []);

    private static EvaluationDimensionPrediction Invalid(string dimensionId) =>
        new(dimensionId, EvaluationDimensionStatuses.Invalid, []);

    private static EvaluationDimensionPrediction Missing(
        string dimensionId,
        string diagnosticCode) =>
        new(
            dimensionId,
            EvaluationDimensionStatuses.Missing,
            [],
            diagnosticCode);

    private static EvaluationDimensionPrediction Invalid(
        string dimensionId,
        string diagnosticCode) =>
        new(
            dimensionId,
            EvaluationDimensionStatuses.Invalid,
            [],
            diagnosticCode);

    private static EvaluationDimensionPrediction Valid(
        string dimensionId,
        IReadOnlyList<string> labels) =>
        new(dimensionId, EvaluationDimensionStatuses.Valid, labels);

    private static string RequiredString(JsonElement element, string property)
    {
        var value = element.GetProperty(property);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Rubric property '{property}' must be a non-empty string.");
        }

        return value.GetString()!;
    }

    private static string[] RequiredLabels(JsonElement element, string property)
    {
        var labels = element.GetProperty(property)
            .EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(item.GetString())
                ? item.GetString()!
                : throw new InvalidDataException(
                    $"Rubric property '{property}' contains an invalid label."))
            .ToArray();
        if (labels.Length == 0
            || labels.Distinct(StringComparer.Ordinal).Count() != labels.Length)
        {
            throw new InvalidDataException(
                $"Rubric property '{property}' must contain unique labels.");
        }

        return labels;
    }

    private static double RequiredScore(JsonElement element, string property)
    {
        var score = element.GetProperty(property).GetDouble();
        return double.IsFinite(score) && score is >= 0d and <= 1d
            ? score
            : throw new InvalidDataException(
                $"Rubric score property '{property}' must be between zero and one.");
    }

    private static double NormalizeScoreBoundary(double score) =>
        Math.Abs(score) <= 1e-12
            ? 0d
            : Math.Abs(score - 1d) <= 1e-12
                ? 1d
                : score;

    private delegate IDimensionScorer ScorerFactory(JsonElement element, string valueKind);

    private readonly record struct ScorerKey(string Id, int Version);

    private sealed record DimensionRuntime(
        EvaluationDimensionDescriptor Descriptor,
        IDimensionScorer Scorer,
        IReadOnlyDictionary<string, string> SourceMapping);

    private interface IDimensionScorer
    {
        IReadOnlyList<string> Labels { get; }

        bool Accepts(IReadOnlyList<string> labels);

        double Score(IReadOnlyList<string> predicted, IReadOnlyList<string> expected);
    }

    private sealed record OrdinalDistanceScorer(
        IReadOnlyList<string> Labels,
        double ExactScore,
        double AdjacentScore,
        double DistantScore) : IDimensionScorer
    {
        public static IDimensionScorer Create(JsonElement element, string valueKind)
        {
            RequireValueKind(valueKind, "single-label", "ordinal-distance");
            var scores = element.GetProperty("distance_scores");
            return new OrdinalDistanceScorer(
                RequiredLabels(element, "ordered_labels"),
                RequiredScore(scores, "0"),
                RequiredScore(scores, "1"),
                RequiredScore(scores, "2_or_more"));
        }

        public bool Accepts(IReadOnlyList<string> labels) =>
            labels.Count == 1 && Labels.Contains(labels[0], StringComparer.Ordinal);

        public double Score(IReadOnlyList<string> predicted, IReadOnlyList<string> expected) =>
            Math.Abs(
                Labels.IndexOf(predicted[0], StringComparer.Ordinal)
                - Labels.IndexOf(expected[0], StringComparer.Ordinal)) switch
            {
                0 => ExactScore,
                1 => AdjacentScore,
                _ => DistantScore,
            };
    }

    private sealed record SetF1Scorer(
        IReadOnlyList<string> Labels,
        double BothEmptyScore,
        double OneEmptyScore) : IDimensionScorer
    {
        public static IDimensionScorer Create(JsonElement element, string valueKind)
        {
            RequireValueKind(valueKind, "label-set", "set-f1");
            if (RequiredString(element, "label_matching") != "exact"
                || RequiredString(element, "duplicate_handling") != "collapse")
            {
                throw new InvalidDataException(
                    "The set-f1 scorer requires exact label matching and duplicate collapse.");
            }

            return new SetF1Scorer(
                RequiredLabels(element, "allowed_labels"),
                RequiredScore(element, "both_sets_empty_score"),
                RequiredScore(element, "one_set_empty_score"));
        }

        public bool Accepts(IReadOnlyList<string> labels) =>
            labels.All(label => Labels.Contains(label, StringComparer.Ordinal));

        public double Score(IReadOnlyList<string> predicted, IReadOnlyList<string> expected)
        {
            var predictedSet = predicted.ToHashSet(StringComparer.Ordinal);
            var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
            if (predictedSet.Count == 0 && expectedSet.Count == 0)
            {
                return BothEmptyScore;
            }

            if (predictedSet.Count == 0 || expectedSet.Count == 0)
            {
                return OneEmptyScore;
            }

            var matches = predictedSet.Intersect(expectedSet, StringComparer.Ordinal).Count();
            var precision = (double)matches / predictedSet.Count;
            var recall = (double)matches / expectedSet.Count;
            return precision + recall == 0d
                ? 0d
                : 2d * precision * recall / (precision + recall);
        }
    }

    private sealed record ExactMatchScorer(
        IReadOnlyList<string> Labels,
        double MatchScore,
        double MismatchScore) : IDimensionScorer
    {
        public static IDimensionScorer Create(JsonElement element, string valueKind)
        {
            RequireValueKind(valueKind, "single-label", "exact-match");
            return new ExactMatchScorer(
                RequiredLabels(element, "allowed_labels"),
                RequiredScore(element, "match_score"),
                RequiredScore(element, "mismatch_score"));
        }

        public bool Accepts(IReadOnlyList<string> labels) =>
            labels.Count == 1 && Labels.Contains(labels[0], StringComparer.Ordinal);

        public double Score(IReadOnlyList<string> predicted, IReadOnlyList<string> expected) =>
            string.Equals(predicted[0], expected[0], StringComparison.Ordinal)
                ? MatchScore
                : MismatchScore;
    }

    private static void RequireValueKind(
        string actual,
        string expected,
        string scorer)
    {
        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Scorer '{scorer}' requires value kind '{expected}'.");
        }
    }
}

public sealed record EvaluationDimensionDescriptor(
    string Id,
    string Source,
    string ValueKind,
    decimal Weight,
    EvaluationScorerDescriptor Scorer,
    IReadOnlyList<string> Labels);

public sealed record EvaluationScorerDescriptor(string Id, int Version);

public sealed record EvaluationAggregationDescriptor(
    string CaseScore,
    string CorpusScore,
    string Rounding);

public static class EvaluationDimensionStatuses
{
    public const string Valid = "valid";
    public const string Missing = "missing";
    public const string Invalid = "invalid";
}

internal static class ReadOnlyListExtensions
{
    public static int IndexOf(
        this IReadOnlyList<string> values,
        string value,
        StringComparer comparer)
    {
        for (var index = 0; index < values.Count; index++)
        {
            if (comparer.Equals(values[index], value))
            {
                return index;
            }
        }

        return -1;
    }
}
