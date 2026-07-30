using System.Text.Json;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationPlanTests
{
    [Fact]
    public void Historical_tracked_plan_preserves_corpus_isolation_and_rejects_post_freeze_code()
    {
        using var stream = File.OpenRead(PlanPath);
        var plan = JsonSerializer.Deserialize<EvaluationPlan>(stream)!;
        var calibration = EvaluationCorpus.Load(Path.Combine(
            RepositoryRoot,
            plan.Calibration.Path.Replace('/', Path.DirectorySeparatorChar)));
        var holdout = EvaluationCorpus.Load(Path.Combine(
            RepositoryRoot,
            plan.Holdout.Path.Replace('/', Path.DirectorySeparatorChar)));

        Assert.Equal("bug-triage-holdout-v1", plan.FreezeId);
        Assert.Equal("us-f0-13-t12-v1", plan.CodeVersion);
        Assert.Equal(45, plan.Provider.TimeoutSeconds);
        Assert.Equal(30, calibration.Cases.Count);
        Assert.Equal(30, holdout.Cases.Count);
        Assert.Empty(calibration.Cases.Select(item => item.CaseId)
            .Intersect(holdout.Cases.Select(item => item.CaseId), StringComparer.Ordinal));
        Assert.Empty(calibration.Cases.Select(item => Normalize(item.Context))
            .Intersect(holdout.Cases.Select(item => Normalize(item.Context)), StringComparer.Ordinal));

        var exception = Assert.Throws<InvalidDataException>(() =>
            EvaluationPlan.Load(PlanPath, EvaluationPlan.CalibrationPartition));
        Assert.Equal(
            "Frozen evaluation-runner-code is missing or outside the repository.",
            exception.Message);
    }

    [Fact]
    public void Analyzer_produces_readiness_quality_matrix_recall_cost_and_latency()
    {
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [
                Case("case-001", "report"),
                Case("case-002", "escalation"),
            ]);
        var cases = new[]
        {
            Result("case-001", "report", 100, 0.01m, 1d),
            Result("case-002", "report", 200, 0.02m, 0d) with
            {
                InvalidOutputDiagnostics = new EvaluationInvalidOutputDiagnostics(
                    1,
                    1,
                    [new("decision.report.body", "invalid-field")]),
            },
        };
        var dataset = new EvaluationDataset(
            1,
            1,
            "calibration-ready-v1",
            "http://localhost:8080",
            120,
            1000,
            cases,
            1,
            1,
            0.85);

        var analysis = EvaluationRunAnalyzer.Analyze(
            corpus,
            dataset,
            PlanForAnalysis(),
            EvaluationPlan.CalibrationPartition);

        Assert.Equal("ready", analysis.Status);
        Assert.Empty(analysis.FailureCodes);
        Assert.Equal(1d, analysis.TerminalCoverage.Rate);
        Assert.Equal(1d, analysis.CostStateCoverage.Rate);
        Assert.Equal(1d, analysis.ProjectionCoverage.Rate);
        Assert.Equal(0.5d, analysis.DimensionQuality.Single().MacroAverage);
        Assert.Equal(1, analysis.DecisionMatrix.Single(item =>
            item.Actual == "escalation" && item.Predicted == "report").Count);
        Assert.Equal(0d, analysis.PositiveRecall.Recall);
        Assert.Equal(0.03m, Assert.Single(analysis.Cost.Totals).Amount);
        Assert.Equal(200, analysis.Latency.P95Milliseconds);
        Assert.Equal(15, analysis.DeadlineCalibration.SourceRightCensoredCount);
        Assert.Equal(45_000, analysis.DeadlineCalibration.SelectedTimeoutMilliseconds);
        Assert.Equal(
            new EvaluationInvalidOutputDiagnosticAggregate(
                "decision.report.body",
                "invalid-field",
                1,
                1),
            Assert.Single(analysis.InvalidOutputDiagnostics));
    }

    [Fact]
    public void Analyzer_refuses_gate_eligibility_when_terminal_cost_projection_or_freeze_drifts()
    {
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [Case("case-001", "escalation")]);
        var drifted = Result("case-001", "escalation", 45_000, 0.01m, 1d) with
        {
            Outcome = "timeout",
            TerminalCode = "timeout",
            ProviderId = "other-provider",
            ModelId = "other-model",
            OutputConstraintMode = "text",
            CostStatus = null,
            PricingVersion = "other-pricing",
            Prediction = null,
            Scoring = null,
        };
        var dataset = new EvaluationDataset(
            1, 1, "holdout-run", "http://localhost:8080", 120, 1000, [drifted]);

        var analysis = EvaluationRunAnalyzer.Analyze(
            corpus,
            dataset,
            PlanForAnalysis(),
            EvaluationPlan.HoldoutPartition);

        Assert.Equal("incomplete", analysis.Status);
        Assert.Equal(
            [
                "cost-state-incomplete",
                "projection-incomplete",
                "terminal-incomplete",
            ],
            analysis.FailureCodes);
        Assert.Equal(1, analysis.Latency.RightCensoredCount);
        Assert.Null(analysis.Latency.P95Milliseconds);
        Assert.Equal(1, analysis.DecisionUnclassifiedCount);
        Assert.Equal(0d, analysis.PositiveRecall.Recall);
    }

    [Fact]
    public void Analyzer_aggregates_envelope_diagnostics_per_case_and_execution()
    {
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [Case("case-001", "report"), Case("case-002", "report")]);
        var cases = new[]
        {
            Result("case-001", "report", 100, 0.01m, 1d) with
            {
                Prediction = new EvaluationPrediction(
                    1,
                    1,
                    [
                        new("decision", EvaluationDimensionStatuses.Valid, ["report"]),
                        new("missing-information", "invalid", [], "unknown-label"),
                        new("severity", "missing", [], "envelope-missing"),
                    ]),
            },
            Result("case-002", "report", 200, 0.02m, 1d) with
            {
                Prediction = new EvaluationPrediction(
                    1,
                    1,
                    [
                        new("decision", EvaluationDimensionStatuses.Valid, ["report"]),
                        new("missing-information", "invalid", [], "unknown-label"),
                        new("severity", "valid", ["medium"]),
                    ]),
            },
        };
        var dataset = new EvaluationDataset(
            1, 1, "calibration-envelope", "http://localhost:8080", 120, 1000, cases, 1, 1, 0.5);

        var analysis = EvaluationRunAnalyzer.Analyze(
            corpus,
            dataset,
            PlanForAnalysis(),
            EvaluationPlan.CalibrationPartition);

        Assert.NotNull(analysis.EnvelopeDiagnostics);
        Assert.Equal(
            [
                new EvaluationEnvelopeDiagnosticAggregate("envelope-missing", "severity", 1, 1),
                new EvaluationEnvelopeDiagnosticAggregate("unknown-label", "missing-information", 2, 2),
            ],
            analysis.EnvelopeDiagnostics);
    }

    [Fact]
    public void Holdout_fixture_is_anonymized_well_formed_and_uses_only_rubric_labels()
    {
        var path = Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "evaluation",
            "bug-triage-holdout-corpus.v1.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var root = document.RootElement;
        var cases = root.GetProperty("cases").EnumerateArray().ToArray();

        Assert.Equal("holdout", root.GetProperty("partition").GetString());
        Assert.Equal("anonymized-historical-reconstructions", root.GetProperty("provenance").GetString());
        Assert.InRange(cases.Length, 30, 50);
        Assert.Equal(cases.Length, cases.Select(item => item.GetProperty("case_id").GetString())
            .Distinct(StringComparer.Ordinal).Count());
        Assert.All(cases, item =>
        {
            Assert.True(item.GetProperty("context").GetString()!.Length >= 120);
            Assert.Equal(
                ["decision", "missing-information", "severity"],
                item.GetProperty("human_reference").EnumerateObject()
                    .Select(property => property.Name)
                    .OrderBy(value => value, StringComparer.Ordinal));
        });
    }

    private static EvaluationPlan PlanForAnalysis() => new()
    {
        PlanVersion = 1,
        FreezeId = "bug-triage-holdout-v1",
        CodeVersion = "us-f0-13-t09e-v1",
        ConfigurationVersion = "bug-triage-v1",
        Provider = new EvaluationProviderFreeze
        {
            ProviderId = "openai",
            ModelIds = ["gpt-5-mini", "gpt-5-mini-2025-08-07"],
            PricingVersion = "pricing-v1",
            OutputConstraintMode = "json-schema",
            TimeoutSeconds = 45,
        },
        DecisionAnalysis = new EvaluationDecisionAnalysisFreeze
        {
            DimensionId = "decision",
            NegativeLabel = "report",
            PositiveLabel = "escalation",
        },
        DeadlineCalibration = new EvaluationDeadlineFreeze
        {
            Method = "nearest-rank-p95-with-right-censored-boundary",
            SourceRunIds = ["model-a-001", "model-a-002"],
            ObservedUncensoredP95Milliseconds = 29_598,
            RightCensoredCount = 15,
            CensoringBoundaryMilliseconds = 30_000,
            OperationalMarginMilliseconds = 15_000,
            SelectedTimeoutMilliseconds = 45_000,
        },
    };

    private static EvaluationCase Case(string id, string decision) => new(
        id,
        "test",
        "A complete synthetic context used only by the unit test and never serialized into the safe evaluation result dataset.",
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["decision"] = [decision],
        });

    private static EvaluationCaseResult Result(
        string id,
        string decision,
        long latency,
        decimal cost,
        double score) => new(
        id,
        Guid.NewGuid(),
        Guid.NewGuid(),
        Guid.NewGuid(),
        "accepted",
        202,
        "succeeded",
        "result-emitted",
        decision,
        "openai",
        "gpt-5-mini-2025-08-07",
        "json-schema",
        10,
        4,
        14,
        false,
        cost,
        "USD",
        true,
        latency,
        latency + 10,
        "estimated",
        "pricing-v1",
        1_000_000,
        0.25m,
        2m,
        new EvaluationPrediction(
            1,
            1,
            [new("decision", EvaluationDimensionStatuses.Valid, [decision])]),
        new EvaluationCaseScoring(
            "scored",
            [],
            [new("decision", EvaluationDimensionStatuses.Valid, [decision], score)],
            score));

    private static string Normalize(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
            .ToLowerInvariant();

    private static string PlanPath => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-evaluation-plan.v1.json");

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
