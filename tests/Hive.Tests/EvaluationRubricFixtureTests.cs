using System.Text.Json;

namespace Hive.Tests;

public sealed class EvaluationRubricFixtureTests
{
    private static readonly string[] ExpectedDimensionIds =
        ["severity", "missing-information", "decision"];

    [Fact]
    public void Rubric_is_a_versioned_example_compatible_with_the_corpus()
    {
        using var rubricDocument = LoadJson(RubricFile);
        using var corpusDocument = LoadJson(CorpusFile);
        var rubric = rubricDocument.RootElement;

        Assert.Equal(1, rubric.GetProperty("rubric_version").GetInt32());
        Assert.Equal("evaluation-example", rubric.GetProperty("fixture_kind").GetString());
        Assert.Equal(
            corpusDocument.RootElement.GetProperty("corpus_version").GetInt32(),
            rubric.GetProperty("applies_to").GetProperty("corpus_version").GetInt32());
        Assert.Equal(
            "cases[].human_reference",
            rubric.GetProperty("applies_to").GetProperty("baseline_path").GetString());
    }

    [Fact]
    public void Human_baseline_requires_independent_consensus_and_is_frozen()
    {
        using var document = LoadJson(RubricFile);
        var baseline = document.RootElement.GetProperty("human_baseline");

        Assert.Equal("independent-review-with-consensus", baseline.GetProperty("method").GetString());
        Assert.Equal(2, baseline.GetProperty("reviewers_required").GetInt32());
        Assert.True(baseline.GetProperty("independent_first_pass").GetBoolean());
        Assert.Equal(
            "consensus-or-third-reviewer-adjudication",
            baseline.GetProperty("resolution").GetString());
        Assert.True(baseline.GetProperty("frozen_before_evaluation_runs").GetBoolean());
        Assert.False(baseline.GetProperty("reviewer_identities_in_fixture").GetBoolean());
    }

    [Fact]
    public void Dimensions_and_aggregation_are_closed_and_weights_sum_to_one()
    {
        using var document = LoadJson(RubricFile);
        var root = document.RootElement;
        var dimensions = root.GetProperty("dimensions").EnumerateArray().ToArray();

        Assert.Equal(ExpectedDimensionIds, dimensions.Select(DimensionId));
        Assert.Equal(1.0, dimensions.Sum(DimensionWeight), precision: 10);
        Assert.All(dimensions, dimension => Assert.InRange(DimensionWeight(dimension), 0.0, 1.0));
        Assert.Equal(
            ["evaluation-envelope", "evaluation-envelope", "result-message-kind"],
            dimensions.Select(dimension => dimension.GetProperty("source").GetString()));
        Assert.Equal(
            ["single-label", "label-set", "single-label"],
            dimensions.Select(dimension => dimension.GetProperty("value_kind").GetString()));

        var scale = root.GetProperty("score_scale");
        Assert.Equal(0.0, scale.GetProperty("minimum").GetDouble());
        Assert.Equal(1.0, scale.GetProperty("maximum").GetDouble());
        Assert.True(scale.GetProperty("higher_is_better").GetBoolean());

        var aggregation = root.GetProperty("aggregation");
        Assert.Equal("weighted-arithmetic-mean", aggregation.GetProperty("case_score").GetString());
        Assert.Equal(
            "unweighted-macro-mean-of-case-scores",
            aggregation.GetProperty("corpus_score").GetString());
        Assert.Equal("none-until-presentation", aggregation.GetProperty("rounding").GetString());
        Assert.Equal(
            ["expected_routing"],
            aggregation.GetProperty("unscored_reference_fields")
                .EnumerateArray()
                .Select(value => value.GetString()));
    }

    [Fact]
    public void Severity_uses_the_declared_ordinal_distance_scores()
    {
        using var document = LoadJson(RubricFile);
        var severity = Dimension(document.RootElement, "severity");

        Assert.Equal("ordinal-distance", severity.GetProperty("scorer").GetString());
        Assert.Equal(
            ["low", "medium", "high", "critical"],
            severity.GetProperty("ordered_labels").EnumerateArray().Select(value => value.GetString()));

        Assert.Equal(1.0, ScoreSeverity(severity, "high", "high"));
        Assert.Equal(0.5, ScoreSeverity(severity, "medium", "high"));
        Assert.Equal(0.0, ScoreSeverity(severity, "low", "critical"));
    }

    [Fact]
    public void Missing_information_uses_set_f1_with_explicit_empty_set_rules()
    {
        using var document = LoadJson(RubricFile);
        var missing = Dimension(document.RootElement, "missing-information");

        Assert.Equal("set-f1", missing.GetProperty("scorer").GetString());
        Assert.Equal("canonical-slug-exact", missing.GetProperty("label_matching").GetString());
        Assert.Equal("collapse", missing.GetProperty("duplicate_handling").GetString());
        var allowedLabels = missing.GetProperty("allowed_labels")
            .EnumerateArray()
            .Select(value => value.GetString()!)
            .ToArray();
        Assert.NotEmpty(allowedLabels);
        Assert.Equal(
            allowedLabels.OrderBy(label => label, StringComparer.Ordinal),
            allowedLabels);
        Assert.Equal(allowedLabels.Length, allowedLabels.Distinct(StringComparer.Ordinal).Count());
        Assert.All(allowedLabels, label => Assert.DoesNotContain('_', label));
        Assert.Contains("correlation-metadata", allowedLabels);
        Assert.Contains("textual-attachments", allowedLabels);
        Assert.DoesNotContain("correlation_metadata", allowedLabels);

        Assert.Equal(1.0, ScoreMissingInformation(missing, [], []));
        Assert.Equal(0.0, ScoreMissingInformation(missing, ["run-log"], []));
        Assert.Equal(
            2.0 / 3.0,
            ScoreMissingInformation(
                missing,
                ["run-log", "environment", "run-log"],
                ["run-log"]),
            precision: 10);
    }

    [Fact]
    public void Decision_is_exact_and_case_and_corpus_aggregation_are_deterministic()
    {
        using var document = LoadJson(RubricFile);
        var root = document.RootElement;
        var decision = Dimension(root, "decision");

        Assert.Equal("exact-match", decision.GetProperty("scorer").GetString());
        Assert.Equal(["report", "escalation"], decision.GetProperty("allowed_labels")
            .EnumerateArray().Select(value => value.GetString()));
        Assert.Equal(1.0, ScoreDecision(decision, "report", "report"));
        Assert.Equal(0.0, ScoreDecision(decision, "report", "escalation"));

        var dimensions = root.GetProperty("dimensions").EnumerateArray().ToArray();
        var firstCase = WeightedCaseScore(dimensions, [1.0, 0.5, 1.0]);
        var secondCase = WeightedCaseScore(dimensions, [0.5, 1.0, 0.0]);

        Assert.Equal(0.825, firstCase, precision: 10);
        Assert.Equal(0.525, secondCase, precision: 10);
        Assert.Equal(0.675, new[] { firstCase, secondCase }.Average(), precision: 10);
    }

    private static double ScoreSeverity(JsonElement dimension, string predicted, string expected)
    {
        var labels = dimension.GetProperty("ordered_labels")
            .EnumerateArray()
            .Select(value => value.GetString())
            .ToArray();
        var distance = Math.Abs(Array.IndexOf(labels, predicted) - Array.IndexOf(labels, expected));
        var scores = dimension.GetProperty("distance_scores");

        return scores.GetProperty(distance >= 2 ? "2_or_more" : distance.ToString()).GetDouble();
    }

    private static double ScoreMissingInformation(
        JsonElement dimension,
        IEnumerable<string> predicted,
        IEnumerable<string> expected)
    {
        var predictedSet = predicted.ToHashSet(StringComparer.Ordinal);
        var expectedSet = expected.ToHashSet(StringComparer.Ordinal);

        if (predictedSet.Count == 0 && expectedSet.Count == 0)
        {
            return dimension.GetProperty("both_sets_empty_score").GetDouble();
        }

        if (predictedSet.Count == 0 || expectedSet.Count == 0)
        {
            return dimension.GetProperty("one_set_empty_score").GetDouble();
        }

        var matches = predictedSet.Intersect(expectedSet, StringComparer.Ordinal).Count();
        var precision = (double)matches / predictedSet.Count;
        var recall = (double)matches / expectedSet.Count;
        return precision + recall == 0 ? 0 : 2 * precision * recall / (precision + recall);
    }

    private static double ScoreDecision(JsonElement dimension, string predicted, string expected) =>
        dimension.GetProperty(predicted == expected ? "match_score" : "mismatch_score").GetDouble();

    private static double WeightedCaseScore(JsonElement[] dimensions, double[] scores) =>
        dimensions.Zip(scores, (dimension, score) => DimensionWeight(dimension) * score).Sum();

    private static JsonElement Dimension(JsonElement root, string id) =>
        root.GetProperty("dimensions").EnumerateArray().Single(item => DimensionId(item) == id);

    private static string DimensionId(JsonElement dimension) =>
        dimension.GetProperty("id").GetString()!;

    private static double DimensionWeight(JsonElement dimension) =>
        dimension.GetProperty("weight").GetDouble();

    private static JsonDocument LoadJson(string path) => JsonDocument.Parse(File.ReadAllText(path));

    private static string RubricFile => Path.Combine(EvaluationDirectory, "bug-triage-rubric.v1.json");

    private static string CorpusFile => Path.Combine(EvaluationDirectory, "bug-triage-corpus.v1.json");

    private static string EvaluationDirectory => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }
}
