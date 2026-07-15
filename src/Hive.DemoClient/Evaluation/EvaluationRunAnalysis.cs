using System.Text.Json.Serialization;

namespace Hive.DemoClient.Evaluation;

public static class EvaluationRunAnalyzer
{
    private static readonly HashSet<string> ExplicitCostStatuses =
        new(StringComparer.Ordinal)
        {
            "provider-reported",
            "estimated",
            "cost-unavailable",
        };

    public static EvaluationRunAnalysis Analyze(
        EvaluationCorpus corpus,
        EvaluationDataset dataset,
        EvaluationPlan plan,
        string partition)
    {
        ArgumentNullException.ThrowIfNull(corpus);
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(plan);

        var total = corpus.Cases.Count;
        var terminalCount = dataset.Cases.Count(IsAuditableTerminal);
        var explicitCostCount = dataset.Cases.Count(item =>
            item.CostStatus is not null && ExplicitCostStatuses.Contains(item.CostStatus));
        var scoreableCount = dataset.Cases.Count(item =>
            item.Scoring?.Status == "scored"
            && item.Scoring.Dimensions.All(dimension =>
                dimension.Status == EvaluationDimensionStatuses.Valid));

        var failures = new HashSet<string>(StringComparer.Ordinal);
        AddCoverageFailure(failures, terminalCount, total, "terminal-incomplete");
        AddCoverageFailure(failures, explicitCostCount, total, "cost-state-incomplete");
        AddCoverageFailure(failures, scoreableCount, total, "projection-incomplete");
        ValidateFrozenRuntime(dataset.Cases, plan, failures);

        var dimensionQuality = dataset.Cases
            .SelectMany(item => item.Scoring?.Dimensions ?? [])
            .GroupBy(item => item.DimensionId, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new EvaluationDimensionQuality(
                group.Key,
                group.Count(),
                group.Average(item => item.Score)))
            .ToArray();
        var matrix = BuildDecisionMatrix(corpus, dataset, plan, failures);
        var invalidOutputDiagnostics = dataset.Cases
            .Where(item => item.InvalidOutputDiagnostics is not null)
            .SelectMany(item => item.InvalidOutputDiagnostics!.Errors.Select(error => new
            {
                item.CaseId,
                error.Path,
                error.Code,
            }))
            .GroupBy(item => (item.Path, item.Code))
            .OrderBy(group => group.Key.Path, StringComparer.Ordinal)
            .ThenBy(group => group.Key.Code, StringComparer.Ordinal)
            .Select(group => new EvaluationInvalidOutputDiagnosticAggregate(
                group.Key.Path,
                group.Key.Code,
                group.Select(item => item.CaseId).Distinct(StringComparer.Ordinal).Count(),
                group.Count()))
            .ToArray();
        var latency = BuildLatency(dataset.Cases);
        var cost = BuildCost(dataset.Cases);
        var deadline = new EvaluationDeadlineAnalysis(
            plan.DeadlineCalibration.Method,
            plan.DeadlineCalibration.SourceRunIds,
            plan.DeadlineCalibration.ObservedUncensoredP95Milliseconds,
            plan.DeadlineCalibration.RightCensoredCount,
            plan.DeadlineCalibration.CensoringBoundaryMilliseconds,
            plan.DeadlineCalibration.OperationalMarginMilliseconds,
            plan.DeadlineCalibration.SelectedTimeoutMilliseconds,
            latency.P95Milliseconds,
            latency.RightCensoredCount);

        var complete = failures.Count == 0;
        var status = complete
            ? partition == EvaluationPlan.CalibrationPartition ? "ready" : "gate-eligible"
            : partition == EvaluationPlan.CalibrationPartition ? "not-ready" : "incomplete";
        return new EvaluationRunAnalysis(
            status,
            failures.OrderBy(value => value, StringComparer.Ordinal).ToArray(),
            Coverage(total, terminalCount),
            Coverage(total, explicitCostCount),
            Coverage(total, scoreableCount),
            dimensionQuality,
            matrix.Rows,
            matrix.UnclassifiedCount,
            matrix.Recall,
            invalidOutputDiagnostics,
            cost,
            latency,
            deadline);
    }

    private static bool IsAuditableTerminal(EvaluationCaseResult item) =>
        item.SubmissionStatus == "accepted"
        && item.Outcome is "succeeded" or "failed" or "rejected"
        && !string.IsNullOrWhiteSpace(item.TerminalCode);

    private static void ValidateFrozenRuntime(
        IReadOnlyList<EvaluationCaseResult> cases,
        EvaluationPlan plan,
        ISet<string> failures)
    {
        var terminal = cases.Where(IsAuditableTerminal).ToArray();
        if (terminal.Any(item =>
            !string.Equals(item.ProviderId, plan.Provider.ProviderId, StringComparison.Ordinal)))
        {
            failures.Add("provider-drift");
        }

        if (terminal.Any(item =>
            item.ModelId is null || !plan.Provider.ModelIds.Contains(item.ModelId, StringComparer.Ordinal)))
        {
            failures.Add("model-drift");
        }

        if (terminal.Any(item =>
            !string.Equals(
                item.OutputConstraintMode,
                plan.Provider.OutputConstraintMode,
                StringComparison.Ordinal)))
        {
            failures.Add("output-constraint-drift");
        }

        if (terminal.Any(item =>
            item.CostStatus == "estimated"
            && !string.Equals(
                item.PricingVersion,
                plan.Provider.PricingVersion,
                StringComparison.Ordinal)))
        {
            failures.Add("pricing-drift");
        }
    }

    private static DecisionMatrixResult BuildDecisionMatrix(
        EvaluationCorpus corpus,
        EvaluationDataset dataset,
        EvaluationPlan plan,
        ISet<string> failures)
    {
        var byId = dataset.Cases.ToDictionary(item => item.CaseId, StringComparer.Ordinal);
        var negative = plan.DecisionAnalysis.NegativeLabel;
        var positive = plan.DecisionAnalysis.PositiveLabel;
        var counts = new Dictionary<(string Actual, string Predicted), int>();
        foreach (var actual in new[] { negative, positive })
        {
            foreach (var predicted in new[] { negative, positive })
            {
                counts[(actual, predicted)] = 0;
            }
        }

        var unclassified = 0;
        var positiveUnclassified = 0;
        foreach (var item in corpus.Cases)
        {
            var actual = SingleLabel(item.HumanReference, plan.DecisionAnalysis.DimensionId);
            if (actual is null || (actual != negative && actual != positive))
            {
                failures.Add("decision-reference-invalid");
                unclassified++;
                continue;
            }

            var predicted = byId.TryGetValue(item.CaseId, out var result)
                ? SinglePredictionLabel(result.Prediction, plan.DecisionAnalysis.DimensionId)
                : null;
            if (predicted is null || (predicted != negative && predicted != positive))
            {
                unclassified++;
                if (actual == positive) positiveUnclassified++;
                continue;
            }

            counts[(actual, predicted)]++;
        }

        var truePositive = counts[(positive, positive)];
        var falseNegative = counts[(positive, negative)] + positiveUnclassified;
        var denominator = truePositive + falseNegative;
        var rows = counts
            .OrderBy(item => item.Key.Actual == negative ? 0 : 1)
            .ThenBy(item => item.Key.Predicted == negative ? 0 : 1)
            .Select(item => new EvaluationDecisionMatrixCell(
                item.Key.Actual,
                item.Key.Predicted,
                item.Value))
            .ToArray();
        return new DecisionMatrixResult(
            rows,
            unclassified,
            new EvaluationPositiveRecall(
                positive,
                truePositive,
                falseNegative,
                denominator == 0 ? null : (double)truePositive / denominator));
    }

    private static EvaluationLatencyAnalysis BuildLatency(
        IReadOnlyList<EvaluationCaseResult> cases)
    {
        var observed = cases
            .Where(item => item.TerminalCode != "timeout")
            .Select(item => item.GatewayLatencyMilliseconds)
            .Where(value => value.HasValue)
            .Select(value => value!.Value)
            .OrderBy(value => value)
            .ToArray();
        return new EvaluationLatencyAnalysis(
            observed.Length,
            cases.Count(item => item.TerminalCode == "timeout"),
            Percentile(observed, 0.50),
            Percentile(observed, 0.95),
            Percentile(observed, 0.99),
            observed.Length == 0 ? null : observed[^1]);
    }

    private static EvaluationCostAnalysis BuildCost(
        IReadOnlyList<EvaluationCaseResult> cases)
    {
        var totals = cases
            .Where(item => item.CostAmount.HasValue && item.CostCurrency is not null)
            .GroupBy(item => item.CostCurrency!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new EvaluationCurrencyTotal(
                group.Key,
                group.Sum(item => item.CostAmount!.Value)))
            .ToArray();
        return new EvaluationCostAnalysis(
            cases.Count(item => item.CostAmount.HasValue),
            cases.Count(item => item.CostStatus == "cost-unavailable"),
            totals);
    }

    private static string? SingleLabel(
        IReadOnlyDictionary<string, IReadOnlyList<string>> values,
        string dimensionId) =>
        values.TryGetValue(dimensionId, out var labels) && labels.Count == 1
            ? labels[0]
            : null;

    private static string? SinglePredictionLabel(
        EvaluationPrediction? prediction,
        string dimensionId)
    {
        var dimension = prediction?.Dimensions
            .SingleOrDefault(item => item.DimensionId == dimensionId);
        return dimension?.Status == EvaluationDimensionStatuses.Valid
            && dimension.Labels.Count == 1
                ? dimension.Labels[0]
                : null;
    }

    private static long? Percentile(IReadOnlyList<long> sorted, double percentile)
    {
        if (sorted.Count == 0) return null;
        var index = Math.Max(0, (int)Math.Ceiling(percentile * sorted.Count) - 1);
        return sorted[index];
    }

    private static EvaluationCoverage Coverage(int total, int complete) =>
        new(total, complete, total == 0 ? 0d : (double)complete / total);

    private static void AddCoverageFailure(
        ISet<string> failures,
        int complete,
        int total,
        string code)
    {
        if (complete != total) failures.Add(code);
    }

    private sealed record DecisionMatrixResult(
        IReadOnlyList<EvaluationDecisionMatrixCell> Rows,
        int UnclassifiedCount,
        EvaluationPositiveRecall Recall);
}

public sealed record EvaluationRunAnalysis(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_codes")] IReadOnlyList<string> FailureCodes,
    [property: JsonPropertyName("terminal_coverage")] EvaluationCoverage TerminalCoverage,
    [property: JsonPropertyName("cost_state_coverage")] EvaluationCoverage CostStateCoverage,
    [property: JsonPropertyName("projection_coverage")] EvaluationCoverage ProjectionCoverage,
    [property: JsonPropertyName("dimension_quality")]
    IReadOnlyList<EvaluationDimensionQuality> DimensionQuality,
    [property: JsonPropertyName("decision_matrix")]
    IReadOnlyList<EvaluationDecisionMatrixCell> DecisionMatrix,
    [property: JsonPropertyName("decision_unclassified_count")] int DecisionUnclassifiedCount,
    [property: JsonPropertyName("positive_recall")] EvaluationPositiveRecall PositiveRecall,
    [property: JsonPropertyName("invalid_output_diagnostics")]
    IReadOnlyList<EvaluationInvalidOutputDiagnosticAggregate> InvalidOutputDiagnostics,
    [property: JsonPropertyName("cost")] EvaluationCostAnalysis Cost,
    [property: JsonPropertyName("latency")] EvaluationLatencyAnalysis Latency,
    [property: JsonPropertyName("deadline_calibration")] EvaluationDeadlineAnalysis DeadlineCalibration);

public sealed record EvaluationCoverage(
    [property: JsonPropertyName("total")] int Total,
    [property: JsonPropertyName("complete")] int Complete,
    [property: JsonPropertyName("rate")] double Rate);

public sealed record EvaluationDimensionQuality(
    [property: JsonPropertyName("dimension_id")] string DimensionId,
    [property: JsonPropertyName("case_count")] int CaseCount,
    [property: JsonPropertyName("macro_average")] double MacroAverage);

public sealed record EvaluationDecisionMatrixCell(
    [property: JsonPropertyName("actual")] string Actual,
    [property: JsonPropertyName("predicted")] string Predicted,
    [property: JsonPropertyName("count")] int Count);

public sealed record EvaluationPositiveRecall(
    [property: JsonPropertyName("positive_label")] string PositiveLabel,
    [property: JsonPropertyName("true_positive")] int TruePositive,
    [property: JsonPropertyName("false_negative")] int FalseNegative,
    [property: JsonPropertyName("recall")] double? Recall);

public sealed record EvaluationInvalidOutputDiagnosticAggregate(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("case_count")] int CaseCount,
    [property: JsonPropertyName("occurrence_count")] int OccurrenceCount);

public sealed record EvaluationCostAnalysis(
    [property: JsonPropertyName("available_count")] int AvailableCount,
    [property: JsonPropertyName("unavailable_count")] int UnavailableCount,
    [property: JsonPropertyName("totals")] IReadOnlyList<EvaluationCurrencyTotal> Totals);

public sealed record EvaluationCurrencyTotal(
    [property: JsonPropertyName("currency")] string Currency,
    [property: JsonPropertyName("amount")] decimal Amount);

public sealed record EvaluationLatencyAnalysis(
    [property: JsonPropertyName("observed_count")] int ObservedCount,
    [property: JsonPropertyName("right_censored_count")] int RightCensoredCount,
    [property: JsonPropertyName("p50_ms")] long? P50Milliseconds,
    [property: JsonPropertyName("p95_ms")] long? P95Milliseconds,
    [property: JsonPropertyName("p99_ms")] long? P99Milliseconds,
    [property: JsonPropertyName("max_ms")] long? MaxMilliseconds);

public sealed record EvaluationDeadlineAnalysis(
    [property: JsonPropertyName("method")] string Method,
    [property: JsonPropertyName("source_run_ids")] IReadOnlyList<string> SourceRunIds,
    [property: JsonPropertyName("source_uncensored_p95_ms")] int SourceUncensoredP95Milliseconds,
    [property: JsonPropertyName("source_right_censored_count")] int SourceRightCensoredCount,
    [property: JsonPropertyName("source_censoring_boundary_ms")] int SourceCensoringBoundaryMilliseconds,
    [property: JsonPropertyName("operational_margin_ms")] int OperationalMarginMilliseconds,
    [property: JsonPropertyName("selected_timeout_ms")] int SelectedTimeoutMilliseconds,
    [property: JsonPropertyName("run_uncensored_p95_ms")] long? RunUncensoredP95Milliseconds,
    [property: JsonPropertyName("run_right_censored_count")] int RunRightCensoredCount);
