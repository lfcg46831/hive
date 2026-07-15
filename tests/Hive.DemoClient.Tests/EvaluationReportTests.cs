using Hive.DemoClient.Evaluation;

namespace Hive.DemoClient.Tests;

public sealed class EvaluationReportTests
{
    [Fact]
    public void Report_options_default_to_curated_evidence()
    {
        var options = EvaluationReportOptions.Parse([], RepositoryRoot);
        var evidenceRoot = Path.Combine(
            RepositoryRoot,
            "evidence",
            "evaluation",
            "bug-triage-holdout-v1");

        Assert.Equal(Path.Combine(evidenceRoot, "holdout-v1.json"), options.DatasetPath);
        Assert.Equal(
            Path.Combine(evidenceRoot, "bug-triage-unit-economics-quality-report.v1.md"),
            options.OutputPath);
    }

    [Fact]
    public void Tracked_holdout_report_calculates_quality_economics_and_two_model_scenarios()
    {
        var report = EvaluationReportBuilder.Build(DatasetPath, ProfilePath);

        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal("holdout", report.Partition);
        Assert.Equal("incomplete", report.EvidenceStatus);
        Assert.False(report.GateEligible);
        Assert.Equal(["projection-incomplete"], report.FailureCodes);
        Assert.Equal(30, report.CaseCount);
        Assert.Equal(0.5751262626262627d, report.CorpusScore);
        Assert.Equal(23, report.ProjectionCoverage.Complete);
        Assert.Equal(0.3d, report.PositiveDecisionRate.PredictedRate);
        Assert.Equal(8d / 30d, report.PositiveDecisionRate.BaselineRate);
        Assert.Equal(1d, report.PositiveDecisionRate.Recall);

        var measured = Assert.Single(report.MeasuredCosts);
        Assert.Equal("USD", measured.Currency);
        Assert.Equal(30, measured.AvailableCount);
        Assert.Equal(0, measured.UnavailableCount);
        Assert.Equal(0.162192m, measured.KnownTotal);
        Assert.Equal(0.0054064m, measured.CostPerWorkItem);
        Assert.Equal(0.27032m, measured.CostPerPositionDay);

        Assert.Collection(
            report.ModelSensitivity,
            mini =>
            {
                Assert.Equal("gpt-5-mini", mini.ModelId);
                Assert.Equal(69_202, mini.InputTokens);
                Assert.Equal(72_444, mini.OutputTokens);
                Assert.Equal(0.16218850m, mini.RepricedTotal);
                Assert.Equal(0.0054062833333333333333333333m, mini.CostPerWorkItem);
                Assert.Equal(0.2703141666666666666666666650m, mini.CostPerPositionDay);
            },
            nano =>
            {
                Assert.Equal("gpt-5-nano", nano.ModelId);
                Assert.Equal(0.03243770m, nano.RepricedTotal);
                Assert.Equal(0.0010812566666666666666666667m, nano.CostPerWorkItem);
                Assert.Equal(0.0540628333333333333333333350m, nano.CostPerPositionDay);
            });
    }

    [Fact]
    public void Renderer_is_deterministic_and_tracked_report_is_current()
    {
        var report = EvaluationReportBuilder.Build(DatasetPath, ProfilePath);
        var rendered = EvaluationReportRenderer.Render(report);
        var tracked = File.ReadAllText(ReportPath).Replace("\r\n", "\n", StringComparison.Ordinal);

        Assert.Equal(rendered, tracked);
        Assert.Contains("Predicted `escalation` rate: **30.00 %**", rendered, StringComparison.Ordinal);
        Assert.Contains("This holdout is not gate-eligible", rendered, StringComparison.Ordinal);
        Assert.Contains("`openai/gpt-5-mini`", rendered, StringComparison.Ordinal);
        Assert.Contains("`openai/gpt-5-nano`", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("US-F0-13-T06 go/no-go decision: go", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public void Builder_rejects_calibration_or_inconsistent_gate_claims()
    {
        var report = EvaluationReportBuilder.Build(DatasetPath, ProfilePath);
        var profile = EvaluationReportProfile.Load(ProfilePath);
        var dataset = LoadDataset();

        var calibration = dataset with { EvaluationPartition = "calibration" };
        var calibrationException = Assert.Throws<InvalidDataException>(() =>
            EvaluationReportBuilder.Build(
                calibration,
                profile,
                report.Dataset.Name,
                report.Dataset.Sha256,
                report.Profile.Name,
                report.Profile.Sha256));
        Assert.Contains("holdout", calibrationException.Message, StringComparison.Ordinal);

        var inconsistent = dataset with
        {
            RunAnalysis = dataset.RunAnalysis! with { Status = "gate-eligible" },
        };
        var inconsistentException = Assert.Throws<InvalidDataException>(() =>
            EvaluationReportBuilder.Build(
                inconsistent,
                profile,
                report.Dataset.Name,
                report.Dataset.Sha256,
                report.Profile.Name,
                report.Profile.Sha256));
        Assert.Contains("incomplete evidence", inconsistentException.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_cost_or_usage_is_visible_and_never_normalized_as_zero()
    {
        var dataset = LoadDataset();
        var cases = dataset.Cases.ToArray();
        cases[0] = cases[0] with
        {
            InputTokens = null,
            OutputTokens = null,
            TotalTokens = null,
            CostAmount = null,
            CostCurrency = null,
            CostStatus = "cost-unavailable",
        };
        var report = EvaluationReportBuilder.Build(
            dataset with { Cases = cases },
            EvaluationReportProfile.Load(ProfilePath),
            "synthetic-holdout.json",
            new string('a', 64),
            "profile.json",
            new string('b', 64));

        var measured = Assert.Single(report.MeasuredCosts);
        Assert.Equal(29, measured.AvailableCount);
        Assert.Equal(1, measured.UnavailableCount);
        Assert.Null(measured.CostPerWorkItem);
        Assert.Null(measured.CostPerPositionDay);
        Assert.All(report.ModelSensitivity, scenario =>
        {
            Assert.Equal(29, scenario.UsageAvailableCount);
            Assert.Equal(1, scenario.UsageUnavailableCount);
            Assert.Null(scenario.CostPerWorkItem);
            Assert.Null(scenario.CostPerPositionDay);
        });

        var rendered = EvaluationReportRenderer.Render(report);
        Assert.Contains("| 29 | 1 |", rendered, StringComparison.Ordinal);
        Assert.Contains("unavailable", rendered, StringComparison.Ordinal);
    }

    private static EvaluationDataset LoadDataset()
    {
        using var stream = File.OpenRead(DatasetPath);
        return System.Text.Json.JsonSerializer.Deserialize<EvaluationDataset>(stream)!;
    }

    private static string DatasetPath => Path.Combine(
        RepositoryRoot,
        "evidence",
        "evaluation",
        "bug-triage-holdout-v1",
        "holdout-v1.json");

    private static string ProfilePath => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-report-profile.v1.json");

    private static string ReportPath => Path.Combine(
        RepositoryRoot,
        "evidence",
        "evaluation",
        "bug-triage-holdout-v1",
        "bug-triage-unit-economics-quality-report.v1.md");

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
