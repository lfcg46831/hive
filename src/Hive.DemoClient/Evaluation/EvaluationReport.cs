using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Hive.DemoClient.Evaluation;

public static class EvaluationReportBuilder
{
    public static EvaluationReport Build(string datasetPath, string profilePath)
    {
        var dataset = LoadDataset(datasetPath);
        var profile = EvaluationReportProfile.Load(profilePath);
        return Build(
            dataset,
            profile,
            Path.GetFileName(datasetPath),
            CanonicalTextSha256(datasetPath),
            Path.GetFileName(profilePath),
            CanonicalTextSha256(profilePath));
    }

    public static EvaluationReport Build(
        EvaluationDataset dataset,
        EvaluationReportProfile profile,
        string datasetName,
        string datasetSha256,
        string profileName,
        string profileSha256)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(profile);
        profile.Validate();
        ValidateDataset(dataset);

        var analysis = dataset.RunAnalysis!;
        var total = dataset.Cases.Count;
        var positiveLabel = analysis.PositiveRecall.PositiveLabel;
        var predictedPositive = analysis.DecisionMatrix
            .Where(item => item.Predicted == positiveLabel)
            .Sum(item => item.Count);
        var baselinePositive = analysis.DecisionMatrix
            .Where(item => item.Actual == positiveLabel)
            .Sum(item => item.Count);
        var positiveDecisionRate = new EvaluationPositiveDecisionRate(
            positiveLabel,
            total,
            predictedPositive,
            total == 0 ? 0d : (double)predictedPositive / total,
            baselinePositive,
            total == 0 ? 0d : (double)baselinePositive / total,
            analysis.DecisionUnclassifiedCount,
            analysis.PositiveRecall.Recall);

        var measuredCosts = BuildMeasuredCosts(dataset.Cases, profile.WorkItemsPerPositionDay);
        var sensitivity = BuildSensitivity(dataset.Cases, profile);
        var gateEligible = string.Equals(
                dataset.EvaluationPartition,
                EvaluationPlan.HoldoutPartition,
                StringComparison.Ordinal)
            && string.Equals(analysis.Status, "gate-eligible", StringComparison.Ordinal)
            && analysis.TerminalCoverage.Complete == total
            && analysis.CostStateCoverage.Complete == total
            && analysis.ProjectionCoverage.Complete == total;

        return new EvaluationReport(
            1,
            profile.ReportId,
            profile.Title,
            dataset.RunId,
            dataset.EvaluationPartition!,
            dataset.FreezeId!,
            dataset.CodeVersion!,
            dataset.ConfigurationVersion!,
            new EvaluationReportInput(datasetName, datasetSha256),
            new EvaluationReportInput(profileName, profileSha256),
            analysis.Status,
            gateEligible,
            analysis.FailureCodes,
            profile.WorkItemSingular,
            profile.WorkItemPlural,
            profile.WorkItemsPerPositionDay,
            total,
            dataset.CorpusScore,
            analysis.TerminalCoverage,
            analysis.CostStateCoverage,
            analysis.ProjectionCoverage,
            analysis.DimensionQuality,
            analysis.DecisionMatrix,
            positiveDecisionRate,
            analysis.InvalidOutputDiagnostics,
            measuredCosts,
            sensitivity,
            analysis.Latency,
            profile.ModelScenarios
                .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
                .ThenBy(item => item.ModelId, StringComparer.Ordinal)
                .ToArray(),
            analysis.EnvelopeDiagnostics);
    }

    private static EvaluationDataset LoadDataset(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Evaluation dataset path is required.", nameof(path));
        }

        try
        {
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize<EvaluationDataset>(stream)
                ?? throw new InvalidDataException("Evaluation dataset is empty.");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "Evaluation dataset is malformed or unavailable.",
                exception);
        }
    }

    private static void ValidateDataset(EvaluationDataset dataset)
    {
        if (dataset.SchemaVersion != 1
            || string.IsNullOrWhiteSpace(dataset.RunId)
            || dataset.Cases is null
            || dataset.Cases.Count == 0
            || dataset.Cases.Any(item => item is null)
            || dataset.Cases.Select(item => item.CaseId)
                .Distinct(StringComparer.Ordinal).Count() != dataset.Cases.Count)
        {
            throw new InvalidDataException(
                "Evaluation report requires a non-empty version 1 dataset with unique cases.");
        }

        if (!string.Equals(
                dataset.EvaluationPartition,
                EvaluationPlan.HoldoutPartition,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(dataset.FreezeId)
            || string.IsNullOrWhiteSpace(dataset.CodeVersion)
            || string.IsNullOrWhiteSpace(dataset.ConfigurationVersion)
            || dataset.RunAnalysis is null)
        {
            throw new InvalidDataException(
                "Evaluation report requires holdout data and its frozen run analysis.");
        }

        var analysis = dataset.RunAnalysis;
        var total = dataset.Cases.Count;
        if (analysis.FailureCodes is null
            || analysis.DimensionQuality is null
            || analysis.DecisionMatrix is null
            || analysis.InvalidOutputDiagnostics is null
            || string.IsNullOrWhiteSpace(analysis.PositiveRecall.PositiveLabel)
            || analysis.DecisionUnclassifiedCount < 0
            || analysis.DecisionMatrix.Sum(item => item.Count)
                + analysis.DecisionUnclassifiedCount != total
            || analysis.TerminalCoverage.Total != total
            || analysis.CostStateCoverage.Total != total
            || analysis.ProjectionCoverage.Total != total)
        {
            throw new InvalidDataException(
                "Evaluation dataset coverage totals do not match its case count.");
        }

        if (analysis.Status is not "gate-eligible" and not "incomplete")
        {
            throw new InvalidDataException(
                "Evaluation holdout analysis has an unsupported evidence status.");
        }

        if (analysis.Status == "gate-eligible"
            && (analysis.FailureCodes.Count != 0
                || analysis.TerminalCoverage.Complete != total
                || analysis.CostStateCoverage.Complete != total
                || analysis.ProjectionCoverage.Complete != total))
        {
            throw new InvalidDataException(
                "Evaluation dataset claims gate eligibility with incomplete evidence.");
        }
    }

    private static IReadOnlyList<EvaluationMeasuredCost> BuildMeasuredCosts(
        IReadOnlyList<EvaluationCaseResult> cases,
        decimal workItemsPerPositionDay)
    {
        var available = cases.Where(IsKnownCost).ToArray();
        var unavailableCount = cases.Count - available.Length;
        var groups = available
            .GroupBy(item => item.CostCurrency!, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .ToArray();
        var completeSingleCurrency = unavailableCount == 0 && groups.Length == 1;
        return groups
            .Select(group =>
            {
                var total = group.Sum(item => item.CostAmount!.Value);
                decimal? perItem = completeSingleCurrency ? total / cases.Count : null;
                return new EvaluationMeasuredCost(
                    group.Key,
                    group.Count(),
                    unavailableCount,
                    total,
                    perItem,
                    perItem * workItemsPerPositionDay);
            })
            .ToArray();
    }

    private static bool IsKnownCost(EvaluationCaseResult item) =>
        item.CostStatus is "provider-reported" or "estimated"
        && item.CostAmount.HasValue
        && !string.IsNullOrWhiteSpace(item.CostCurrency);

    private static IReadOnlyList<EvaluationModelSensitivity> BuildSensitivity(
        IReadOnlyList<EvaluationCaseResult> cases,
        EvaluationReportProfile profile)
    {
        var usageAvailable = cases
            .Where(item => item.InputTokens.HasValue && item.OutputTokens.HasValue)
            .ToArray();
        var usageUnavailableCount = cases.Count - usageAvailable.Length;
        var inputTokens = usageAvailable.Sum(item => (long)item.InputTokens!.Value);
        var outputTokens = usageAvailable.Sum(item => (long)item.OutputTokens!.Value);
        return profile.ModelScenarios
            .OrderBy(item => item.ProviderId, StringComparer.Ordinal)
            .ThenBy(item => item.ModelId, StringComparer.Ordinal)
            .Select(scenario =>
            {
                var total =
                    (inputTokens / (decimal)scenario.TokenUnit
                        * scenario.InputPricePerTokenUnit)
                    + (outputTokens / (decimal)scenario.TokenUnit
                        * scenario.OutputPricePerTokenUnit);
                decimal? perItem = usageUnavailableCount == 0 ? total / cases.Count : null;
                return new EvaluationModelSensitivity(
                    scenario.ProviderId,
                    scenario.ModelId,
                    scenario.PricingVersion,
                    scenario.Currency,
                    scenario.TokenUnit,
                    scenario.InputPricePerTokenUnit,
                    scenario.OutputPricePerTokenUnit,
                    usageAvailable.Length,
                    usageUnavailableCount,
                    inputTokens,
                    outputTokens,
                    total,
                    perItem,
                    perItem * profile.WorkItemsPerPositionDay);
            })
            .ToArray();
    }

    private static string CanonicalTextSha256(string path)
    {
        try
        {
            var normalized = File.ReadAllText(path, Encoding.UTF8)
                .Replace("\r\n", "\n", StringComparison.Ordinal)
                .Replace("\r", "\n", StringComparison.Ordinal);
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
                .ToLowerInvariant();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException("Evaluation report input cannot be hashed.", exception);
        }
    }
}

public static class EvaluationReportRenderer
{
    public static string Render(EvaluationReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        var builder = new StringBuilder();
        builder.AppendLine($"<!-- evaluation-report-schema-version: {report.SchemaVersion} -->");
        builder.AppendLine();
        builder.AppendLine($"# {Escape(report.Title)}");
        builder.AppendLine();
        builder.AppendLine(
            "This is the versioned US-F0-13-T05 evidence artefact. It measures the frozen holdout and does not define thresholds or make the US-F0-13-T06 go/no-go decision.");
        builder.AppendLine();
        builder.AppendLine("## Evidence");
        builder.AppendLine();
        builder.AppendLine("| Field | Value |");
        builder.AppendLine("| --- | --- |");
        Row(builder, "Report id", Code(report.ReportId));
        Row(builder, "Run id", Code(report.RunId));
        Row(builder, "Partition", Code(report.Partition));
        Row(builder, "Freeze id", Code(report.FreezeId));
        Row(builder, "Code version", Code(report.CodeVersion));
        Row(builder, "Configuration version", Code(report.ConfigurationVersion));
        Row(builder, "Dataset", $"{Code(report.Dataset.Name)} (`normalized-text-sha256:{report.Dataset.Sha256}`)");
        Row(builder, "Report profile", $"{Code(report.Profile.Name)} (`normalized-text-sha256:{report.Profile.Sha256}`)");
        Row(builder, "Evidence status", Code(report.EvidenceStatus));
        Row(builder, "Gate eligible", report.GateEligible ? "yes" : "no");
        Row(
            builder,
            "Failure codes",
            report.FailureCodes.Count == 0
                ? "none"
                : string.Join(", ", report.FailureCodes.Select(Code)));
        builder.AppendLine();
        builder.AppendLine("## Quality");
        builder.AppendLine();
        builder.AppendLine("| Metric | Complete | Total | Rate |");
        builder.AppendLine("| --- | ---: | ---: | ---: |");
        CoverageRow(builder, "Auditable terminal", report.TerminalCoverage);
        CoverageRow(builder, "Explicit cost state", report.CostStateCoverage);
        CoverageRow(builder, "Scoreable projection", report.ProjectionCoverage);
        builder.AppendLine();
        builder.AppendLine($"Corpus macro score: **{Number(report.CorpusScore)}**.");
        builder.AppendLine();
        builder.AppendLine("| Dimension | Cases | Macro agreement |");
        builder.AppendLine("| --- | ---: | ---: |");
        foreach (var dimension in report.DimensionQuality)
        {
            builder.AppendLine(
                $"| {Code(dimension.DimensionId)} | {dimension.CaseCount} | {Number(dimension.MacroAverage)} |");
        }

        builder.AppendLine();
        builder.AppendLine("### Decision analysis");
        builder.AppendLine();
        builder.AppendLine("| Baseline | Predicted | Cases |");
        builder.AppendLine("| --- | --- | ---: |");
        foreach (var cell in report.DecisionMatrix)
        {
            builder.AppendLine(
                $"| {Code(cell.Actual)} | {Code(cell.Predicted)} | {cell.Count} |");
        }

        builder.AppendLine();
        builder.AppendLine(
            $"Predicted {Code(report.PositiveDecisionRate.PositiveLabel)} rate: **{Percent(report.PositiveDecisionRate.PredictedRate)}** ({report.PositiveDecisionRate.PredictedCount}/{report.PositiveDecisionRate.TotalCount}); baseline rate: **{Percent(report.PositiveDecisionRate.BaselineRate)}** ({report.PositiveDecisionRate.BaselineCount}/{report.PositiveDecisionRate.TotalCount}); recall: **{Percent(report.PositiveDecisionRate.Recall)}**; unclassified: **{report.PositiveDecisionRate.UnclassifiedCount}**.");
        builder.AppendLine();
        if (report.InvalidOutputDiagnostics.Count == 0)
        {
            builder.AppendLine("Invalid-output diagnostics: **none**.");
        }
        else
        {
            builder.AppendLine("| Invalid-output path | Code | Cases | Occurrences |");
            builder.AppendLine("| --- | --- | ---: | ---: |");
            foreach (var diagnostic in report.InvalidOutputDiagnostics)
            {
                builder.AppendLine(
                    $"| {Code(diagnostic.Path)} | {Code(diagnostic.Code)} | {diagnostic.CaseCount} | {diagnostic.OccurrenceCount} |");
            }
        }

        // Datasets recorded before US-F0-13-T12c carry no envelope diagnostics; their
        // tracked reports must re-render byte-identically, so the section only exists
        // when the run analysis declared the aggregate.
        if (report.EnvelopeDiagnostics is { } envelopeDiagnostics)
        {
            builder.AppendLine();
            if (envelopeDiagnostics.Count == 0)
            {
                builder.AppendLine("Envelope diagnostics: **none**.");
            }
            else
            {
                builder.AppendLine("| Envelope code | Dimension | Cases | Occurrences |");
                builder.AppendLine("| --- | --- | ---: | ---: |");
                foreach (var diagnostic in envelopeDiagnostics)
                {
                    builder.AppendLine(
                        $"| {Code(diagnostic.Code)} | {Code(diagnostic.DimensionId)} | {diagnostic.CaseCount} | {diagnostic.OccurrenceCount} |");
                }
            }
        }

        builder.AppendLine();
        builder.AppendLine("## Unit economics");
        builder.AppendLine();
        builder.AppendLine(
            $"Daily projection assumption: **{Decimal(report.WorkItemsPerPositionDay)} {Escape(report.WorkItemPlural)} per position/day**.");
        builder.AppendLine();
        builder.AppendLine($"| Currency | Costed {Escape(report.WorkItemPlural)} | Unavailable | Known total | Cost/{Escape(report.WorkItemSingular)} | Cost/position/day |");
        builder.AppendLine("| --- | ---: | ---: | ---: | ---: | ---: |");
        if (report.MeasuredCosts.Count == 0)
        {
            builder.AppendLine("| n/a | 0 | " + report.CaseCount + " | n/a | unavailable | unavailable |");
        }
        else
        {
            foreach (var cost in report.MeasuredCosts)
            {
                builder.AppendLine(
                    $"| {Code(cost.Currency)} | {cost.AvailableCount} | {cost.UnavailableCount} | {Money(cost.KnownTotal)} | {Money(cost.CostPerWorkItem)} | {Money(cost.CostPerPositionDay)} |");
            }
        }

        builder.AppendLine();
        builder.AppendLine("### Model cost sensitivity");
        builder.AppendLine();
        builder.AppendLine(
            "The scenarios reprice observed input/output token usage only. They do not estimate the alternative model's quality, output length, latency, cached-token mix, or operational behaviour.");
        builder.AppendLine();
        builder.AppendLine($"| Provider/model | Pricing | Usage complete | Input tokens | Output tokens | Repriced total | Cost/{Escape(report.WorkItemSingular)} | Cost/position/day |");
        builder.AppendLine("| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |");
        foreach (var scenario in report.ModelSensitivity)
        {
            builder.AppendLine(
                $"| {Code($"{scenario.ProviderId}/{scenario.ModelId}")} | {Code(scenario.PricingVersion)} ({Decimal(scenario.InputPricePerTokenUnit)}/{Decimal(scenario.OutputPricePerTokenUnit)} {scenario.Currency} per {scenario.TokenUnit} input/output tokens) | {scenario.UsageAvailableCount}/{report.CaseCount} | {scenario.InputTokens} | {scenario.OutputTokens} | {Money(scenario.RepricedTotal)} {scenario.Currency} | {Money(scenario.CostPerWorkItem)} | {Money(scenario.CostPerPositionDay)} |");
        }

        builder.AppendLine();
        builder.AppendLine("## Latency");
        builder.AppendLine();
        builder.AppendLine("| Observed | Right-censored | p50 | p95 | p99 | Max |");
        builder.AppendLine("| ---: | ---: | ---: | ---: | ---: | ---: |");
        builder.AppendLine(
            $"| {report.Latency.ObservedCount} | {report.Latency.RightCensoredCount} | {Milliseconds(report.Latency.P50Milliseconds)} | {Milliseconds(report.Latency.P95Milliseconds)} | {Milliseconds(report.Latency.P99Milliseconds)} | {Milliseconds(report.Latency.MaxMilliseconds)} |");
        builder.AppendLine();
        builder.AppendLine("## Limitations and interpretation");
        builder.AppendLine();
        if (!report.GateEligible)
        {
            builder.AppendLine(
                "- This holdout is not gate-eligible. Its quality figures describe the observed run, but US-F0-13-T06 must not use it for a go/no-go decision.");
        }
        else
        {
            builder.AppendLine(
                "- The holdout is gate-eligible as evidence, but threshold comparison and the decision remain exclusively in US-F0-13-T06.");
        }

        builder.AppendLine(
            "- A missing cost or token observation is never treated as zero; affected normalized projections are shown as unavailable.");
        builder.AppendLine(
            $"- The position/day projection is linear at the profile assumption of {Decimal(report.WorkItemsPerPositionDay)} {Escape(report.WorkItemPlural)} and excludes non-model infrastructure, human review, retries, storage, and support costs.");
        builder.AppendLine(
            "- Model sensitivity is a token-price scenario, not evidence that another model preserves the measured quality or latency.");
        builder.AppendLine();
        builder.AppendLine("## Pricing sources");
        builder.AppendLine();
        foreach (var source in report.PricingSources)
        {
            builder.AppendLine(
                $"- {Code($"{source.ProviderId}/{source.ModelId}")}: [{Escape(source.PricingVersion)}]({source.SourceUrl}), accessed {source.SourceAccessedOn}.");
        }

        return builder.ToString().Replace("\r\n", "\n", StringComparison.Ordinal);
    }

    private static void CoverageRow(
        StringBuilder builder,
        string label,
        EvaluationCoverage coverage) =>
        builder.AppendLine(
            $"| {label} | {coverage.Complete} | {coverage.Total} | {Percent(coverage.Rate)} |");

    private static void Row(StringBuilder builder, string field, string value) =>
        builder.AppendLine($"| {field} | {value} |");

    private static string Code(string value) =>
        $"`{Escape(value.Replace("`", "", StringComparison.Ordinal))}`";

    private static string Escape(string value) =>
        value.Replace("|", "\\|", StringComparison.Ordinal).Replace("\r", " ").Replace("\n", " ");

    private static string Percent(double? value) =>
        value.HasValue ? $"{value.Value.ToString("P2", CultureInfo.InvariantCulture)}" : "unavailable";

    private static string Number(double? value) =>
        value.HasValue ? value.Value.ToString("0.0000", CultureInfo.InvariantCulture) : "unavailable";

    private static string Decimal(decimal value) =>
        value.ToString("0.############################", CultureInfo.InvariantCulture);

    private static string Money(decimal? value) =>
        value.HasValue ? value.Value.ToString("0.000000", CultureInfo.InvariantCulture) : "unavailable";

    private static string Milliseconds(long? value) =>
        value.HasValue ? $"{value.Value} ms" : "unavailable";
}

public sealed record EvaluationReport(
    int SchemaVersion,
    string ReportId,
    string Title,
    string RunId,
    string Partition,
    string FreezeId,
    string CodeVersion,
    string ConfigurationVersion,
    EvaluationReportInput Dataset,
    EvaluationReportInput Profile,
    string EvidenceStatus,
    bool GateEligible,
    IReadOnlyList<string> FailureCodes,
    string WorkItemSingular,
    string WorkItemPlural,
    decimal WorkItemsPerPositionDay,
    int CaseCount,
    double? CorpusScore,
    EvaluationCoverage TerminalCoverage,
    EvaluationCoverage CostStateCoverage,
    EvaluationCoverage ProjectionCoverage,
    IReadOnlyList<EvaluationDimensionQuality> DimensionQuality,
    IReadOnlyList<EvaluationDecisionMatrixCell> DecisionMatrix,
    EvaluationPositiveDecisionRate PositiveDecisionRate,
    IReadOnlyList<EvaluationInvalidOutputDiagnosticAggregate> InvalidOutputDiagnostics,
    IReadOnlyList<EvaluationMeasuredCost> MeasuredCosts,
    IReadOnlyList<EvaluationModelSensitivity> ModelSensitivity,
    EvaluationLatencyAnalysis Latency,
    IReadOnlyList<EvaluationModelCostScenario> PricingSources,
    IReadOnlyList<EvaluationEnvelopeDiagnosticAggregate>? EnvelopeDiagnostics = null);

public sealed record EvaluationReportInput(string Name, string Sha256);

public sealed record EvaluationPositiveDecisionRate(
    string PositiveLabel,
    int TotalCount,
    int PredictedCount,
    double PredictedRate,
    int BaselineCount,
    double BaselineRate,
    int UnclassifiedCount,
    double? Recall);

public sealed record EvaluationMeasuredCost(
    string Currency,
    int AvailableCount,
    int UnavailableCount,
    decimal KnownTotal,
    decimal? CostPerWorkItem,
    decimal? CostPerPositionDay);

public sealed record EvaluationModelSensitivity(
    string ProviderId,
    string ModelId,
    string PricingVersion,
    string Currency,
    int TokenUnit,
    decimal InputPricePerTokenUnit,
    decimal OutputPricePerTokenUnit,
    int UsageAvailableCount,
    int UsageUnavailableCount,
    long InputTokens,
    long OutputTokens,
    decimal RepricedTotal,
    decimal? CostPerWorkItem,
    decimal? CostPerPositionDay);
