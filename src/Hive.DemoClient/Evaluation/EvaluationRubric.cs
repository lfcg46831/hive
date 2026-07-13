using System.Text.Json;
using System.Text.RegularExpressions;

namespace Hive.DemoClient.Evaluation;

public sealed partial class EvaluationRubric
{
    private const int SupportedProjectionVersion = 1;
    private readonly SeverityDimension _severity;
    private readonly MissingInformationDimension _missingInformation;
    private readonly DecisionDimension _decision;

    private EvaluationRubric(
        int rubricVersion,
        int corpusVersion,
        SeverityDimension severity,
        MissingInformationDimension missingInformation,
        DecisionDimension decision)
    {
        RubricVersion = rubricVersion;
        CorpusVersion = corpusVersion;
        _severity = severity;
        _missingInformation = missingInformation;
        _decision = decision;
    }

    public int RubricVersion { get; }

    public int CorpusVersion { get; }

    public static EvaluationRubric Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Rubric path is required.", nameof(path));
        }

        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var dimensions = root.GetProperty("dimensions")
            .EnumerateArray()
            .ToDictionary(
                item => RequiredString(item, "id"),
                item => item.Clone(),
                StringComparer.Ordinal);

        if (root.GetProperty("fixture_kind").GetString() != "evaluation-example"
            || root.GetProperty("aggregation").GetProperty("case_score").GetString()
                != "weighted-arithmetic-mean"
            || root.GetProperty("aggregation").GetProperty("corpus_score").GetString()
                != "unweighted-macro-mean-of-case-scores"
            || root.GetProperty("aggregation").GetProperty("rounding").GetString()
                != "none-until-presentation"
            || dimensions.Count != 3)
        {
            throw new InvalidDataException("Evaluation rubric has an unsupported contract.");
        }

        var severity = dimensions["severity"];
        var missing = dimensions["missing-information"];
        var decision = dimensions["decision"];
        if (RequiredString(severity, "scorer") != "ordinal-distance"
            || RequiredString(missing, "scorer") != "set-f1"
            || RequiredString(missing, "label_matching") != "canonical-slug-exact"
            || RequiredString(missing, "duplicate_handling") != "collapse"
            || RequiredString(decision, "scorer") != "exact-match")
        {
            throw new InvalidDataException("Evaluation rubric declares unsupported scorers.");
        }

        var loaded = new EvaluationRubric(
            root.GetProperty("rubric_version").GetInt32(),
            root.GetProperty("applies_to").GetProperty("corpus_version").GetInt32(),
            new SeverityDimension(
                RequiredWeight(severity),
                RequiredStrings(severity, "ordered_labels"),
                severity.GetProperty("distance_scores").GetProperty("0").GetDouble(),
                severity.GetProperty("distance_scores").GetProperty("1").GetDouble(),
                severity.GetProperty("distance_scores").GetProperty("2_or_more").GetDouble()),
            new MissingInformationDimension(
                RequiredWeight(missing),
                RequiredStrings(missing, "allowed_labels"),
                missing.GetProperty("both_sets_empty_score").GetDouble(),
                missing.GetProperty("one_set_empty_score").GetDouble()),
            new DecisionDimension(
                RequiredWeight(decision),
                RequiredStrings(decision, "allowed_labels"),
                decision.GetProperty("match_score").GetDouble(),
                decision.GetProperty("mismatch_score").GetDouble()));

        var totalWeight = loaded._severity.Weight
            + loaded._missingInformation.Weight
            + loaded._decision.Weight;
        if (loaded.RubricVersion != 1
            || loaded.CorpusVersion != 1
            || Math.Abs(totalWeight - 1d) > 1e-12
            || !IsCanonicalVocabulary(loaded._severity.OrderedLabels)
            || !IsCanonicalVocabulary(
                loaded._missingInformation.AllowedLabels,
                requireLexicalOrder: true)
            || !IsCanonicalVocabulary(loaded._decision.AllowedLabels))
        {
            throw new InvalidDataException("Evaluation rubric version or weights are invalid.");
        }

        return loaded;
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

    public EvaluationCaseScoring Score(
        EvaluationHumanReference reference,
        EvaluationPrediction? prediction,
        string? decision)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ValidateReference(reference);

        var failures = new HashSet<string>(StringComparer.Ordinal);
        var versionSupported = prediction?.ProjectionVersion == SupportedProjectionVersion;
        if (prediction is not null && !versionSupported)
        {
            failures.Add("projection-version-unsupported");
        }

        var severityScore = versionSupported
            ? ScoreSeverity(prediction!.Severity, reference.Severity, failures)
            : prediction is null
                ? Missing("severity-prediction-missing", failures)
                : 0d;
        var missingInformationScore = versionSupported
            ? ScoreMissingInformation(
                prediction!.MissingInformation,
                reference.MissingInformation,
                failures)
            : prediction is null
                ? Missing("missing-information-prediction-missing", failures)
                : 0d;
        var decisionScore = ScoreDecision(decision, reference.ExpectedDecision, failures);
        var caseScore =
            (_severity.Weight * severityScore)
            + (_missingInformation.Weight * missingInformationScore)
            + (_decision.Weight * decisionScore);

        return new EvaluationCaseScoring(
            failures.Count == 0 ? "scored" : "failed",
            failures.OrderBy(code => code, StringComparer.Ordinal).ToArray(),
            new EvaluationDimensionScores(
                severityScore,
                missingInformationScore,
                decisionScore),
            caseScore);
    }

    private double ScoreSeverity(
        string? predicted,
        string expected,
        ISet<string> failures)
    {
        if (predicted is null)
        {
            return Missing("severity-prediction-missing", failures);
        }

        var predictedIndex = Array.IndexOf(_severity.OrderedLabels, predicted);
        var expectedIndex = Array.IndexOf(_severity.OrderedLabels, expected);
        if (predictedIndex < 0 || expectedIndex < 0)
        {
            failures.Add("severity-prediction-invalid");
            return 0d;
        }

        return Math.Abs(predictedIndex - expectedIndex) switch
        {
            0 => _severity.ExactScore,
            1 => _severity.AdjacentScore,
            _ => _severity.DistantScore,
        };
    }

    private double ScoreMissingInformation(
        IReadOnlyList<string>? predicted,
        IReadOnlyList<string> expected,
        ISet<string> failures)
    {
        if (predicted is null)
        {
            return Missing("missing-information-prediction-missing", failures);
        }

        if (predicted.Any(label =>
                string.IsNullOrEmpty(label)
                || !CanonicalLabel().IsMatch(label)
                || !_missingInformation.AllowedLabels.Contains(label, StringComparer.Ordinal)))
        {
            failures.Add("missing-information-prediction-invalid");
            return 0d;
        }

        var predictedSet = predicted.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);
        if (predictedSet.Count == 0 && expectedSet.Count == 0)
        {
            return _missingInformation.BothEmptyScore;
        }

        if (predictedSet.Count == 0 || expectedSet.Count == 0)
        {
            return _missingInformation.OneEmptyScore;
        }

        var matches = predictedSet.Intersect(expectedSet, StringComparer.Ordinal).Count();
        var precision = (double)matches / predictedSet.Count;
        var recall = (double)matches / expectedSet.Count;
        return precision + recall == 0d
            ? 0d
            : 2d * precision * recall / (precision + recall);
    }

    private double ScoreDecision(
        string? predicted,
        string expected,
        ISet<string> failures)
    {
        if (predicted is null)
        {
            return Missing("decision-prediction-missing", failures);
        }

        if (!_decision.AllowedLabels.Contains(predicted, StringComparer.Ordinal))
        {
            failures.Add("decision-prediction-invalid");
            return 0d;
        }

        return string.Equals(predicted, expected, StringComparison.Ordinal)
            ? _decision.MatchScore
            : _decision.MismatchScore;
    }

    private static double Missing(string code, ISet<string> failures)
    {
        failures.Add(code);
        return 0d;
    }

    private void ValidateReference(EvaluationHumanReference reference)
    {
        if (!_severity.OrderedLabels.Contains(reference.Severity, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Reference severity '{reference.Severity}' is not declared by the rubric.");
        }

        if (reference.MissingInformation is null
            || reference.MissingInformation.Any(label =>
                string.IsNullOrEmpty(label)
                || !CanonicalLabel().IsMatch(label)
                || !_missingInformation.AllowedLabels.Contains(label, StringComparer.Ordinal))
            || reference.MissingInformation.Distinct(StringComparer.Ordinal).Count()
                != reference.MissingInformation.Count)
        {
            throw new InvalidDataException(
                "Reference missing-information labels are not declared uniquely by the rubric.");
        }

        if (!_decision.AllowedLabels.Contains(reference.ExpectedDecision, StringComparer.Ordinal))
        {
            throw new InvalidDataException(
                $"Reference decision '{reference.ExpectedDecision}' is not declared by the rubric.");
        }
    }

    private static bool IsCanonicalVocabulary(
        string[] labels,
        bool requireLexicalOrder = false) =>
        labels.Length > 0
        && labels.All(label => CanonicalLabel().IsMatch(label))
        && labels.Distinct(StringComparer.Ordinal).Count() == labels.Length
        && (!requireLexicalOrder
            || labels.SequenceEqual(
                labels.OrderBy(label => label, StringComparer.Ordinal),
                StringComparer.Ordinal));

    private static string RequiredString(JsonElement element, string property) =>
        element.GetProperty(property).GetString()
        ?? throw new InvalidDataException($"Rubric property '{property}' is required.");

    private static string[] RequiredStrings(JsonElement element, string property) =>
        element.GetProperty(property)
            .EnumerateArray()
            .Select(item => item.GetString()
                ?? throw new InvalidDataException($"Rubric property '{property}' contains a null label."))
            .ToArray();

    private static double RequiredWeight(JsonElement element)
    {
        var weight = element.GetProperty("weight").GetDouble();
        return weight is > 0d and <= 1d
            ? weight
            : throw new InvalidDataException("Rubric dimension weight is invalid.");
    }

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalLabel();

    private sealed record SeverityDimension(
        double Weight,
        string[] OrderedLabels,
        double ExactScore,
        double AdjacentScore,
        double DistantScore);

    private sealed record MissingInformationDimension(
        double Weight,
        string[] AllowedLabels,
        double BothEmptyScore,
        double OneEmptyScore);

    private sealed record DecisionDimension(
        double Weight,
        string[] AllowedLabels,
        double MatchScore,
        double MismatchScore);
}
