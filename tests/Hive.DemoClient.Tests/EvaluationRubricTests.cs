using Hive.DemoClient.Evaluation;

namespace Hive.DemoClient.Tests;

public sealed class EvaluationRubricTests
{
    private readonly EvaluationRubric _rubric = EvaluationRubric.Load(RubricFile);

    [Fact]
    public void Scores_all_three_dimensions_and_macro_mean_without_rounding()
    {
        var exact = _rubric.Score(
            new EvaluationHumanReference("high", ["environment", "run-log"], "test", "report"),
            new EvaluationPrediction(1, "high", ["run-log", "environment", "run-log"]),
            "report");
        var partial = _rubric.Score(
            new EvaluationHumanReference("high", ["environment", "run-log"], "test", "report"),
            new EvaluationPrediction(1, "medium", ["run-log"]),
            "escalation");

        Assert.Equal("scored", exact.Status);
        Assert.Equal(1d, exact.CaseScore);
        Assert.Equal(0.5d, partial.Dimensions.Severity);
        Assert.Equal(2d / 3d, partial.Dimensions.MissingInformation);
        Assert.Equal(0d, partial.Dimensions.Decision);
        Assert.Equal((0.35d * 0.5d) + (0.35d * (2d / 3d)), partial.CaseScore);
        Assert.Equal((exact.CaseScore + partial.CaseScore) / 2d,
            new[] { exact, partial }.Average(item => item.CaseScore));
    }

    [Fact]
    public void Missing_or_invalid_predictions_fail_structurally_and_zero_affected_dimensions()
    {
        var missing = _rubric.Score(
            new EvaluationHumanReference("medium", [], "test", "report"),
            prediction: null,
            decision: "report");
        var invalid = _rubric.Score(
            new EvaluationHumanReference("medium", [], "test", "report"),
            new EvaluationPrediction(2, "medium", []),
            "report");

        Assert.Equal("failed", missing.Status);
        Assert.Contains("severity-prediction-missing", missing.FailureCodes);
        Assert.Contains("missing-information-prediction-missing", missing.FailureCodes);
        Assert.Equal(0d, missing.Dimensions.Severity);
        Assert.Equal(0d, missing.Dimensions.MissingInformation);
        Assert.Equal(1d, missing.Dimensions.Decision);
        Assert.Equal(0.30d, missing.CaseScore);
        Assert.Equal("failed", invalid.Status);
        Assert.Contains("projection-version-unsupported", invalid.FailureCodes);
        Assert.Equal(0d, invalid.Dimensions.Severity);
        Assert.Equal(0d, invalid.Dimensions.MissingInformation);
    }

    [Theory]
    [InlineData("correlation_metadata")]
    [InlineData("unknown-label")]
    public void Unknown_or_noncanonical_prediction_zeros_only_missing_information(
        string invalidLabel)
    {
        var scoring = _rubric.Score(
            new EvaluationHumanReference("high", ["environment"], "test", "report"),
            new EvaluationPrediction(1, "high", ["environment", invalidLabel]),
            "report");

        Assert.Equal("failed", scoring.Status);
        Assert.Equal(["missing-information-prediction-invalid"], scoring.FailureCodes);
        Assert.Equal(1d, scoring.Dimensions.Severity);
        Assert.Equal(0d, scoring.Dimensions.MissingInformation);
        Assert.Equal(1d, scoring.Dimensions.Decision);
        Assert.Equal(0.65d, scoring.CaseScore, precision: 10);
    }

    [Fact]
    public void Corpus_validation_rejects_reference_labels_outside_the_rubric()
    {
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [
                new EvaluationCase(
                    "triage-999",
                    "test",
                    "A sufficiently descriptive test context for direct rubric validation.",
                    new EvaluationHumanReference(
                        "high",
                        ["correlation_metadata"],
                        "test",
                        "report")),
            ]);

        var exception = Assert.Throws<InvalidDataException>(() => _rubric.ValidateCorpus(corpus));

        Assert.Contains("triage-999", exception.Message, StringComparison.Ordinal);
    }

    private static string RubricFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln"))) return current.FullName;
                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate repository root.");
        }
    }
}
