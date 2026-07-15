using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hive.DemoClient.Evaluation;

public sealed class EvaluationPlan
{
    public const string CalibrationPartition = "calibration";
    public const string HoldoutPartition = "holdout";

    private const string ExpectedFixtureKind = "evaluation-calibration-holdout";
    private const string ExpectedFreezeStatus = "frozen";
    private const string ExpectedDeadlineMethod =
        "nearest-rank-p95-with-right-censored-boundary";

    [JsonPropertyName("plan_version")]
    public int PlanVersion { get; init; }

    [JsonPropertyName("fixture_kind")]
    public string FixtureKind { get; init; } = string.Empty;

    [JsonPropertyName("freeze_id")]
    public string FreezeId { get; init; } = string.Empty;

    [JsonPropertyName("freeze_status")]
    public string FreezeStatus { get; init; } = string.Empty;

    [JsonPropertyName("code_version")]
    public string CodeVersion { get; init; } = string.Empty;

    [JsonPropertyName("configuration_version")]
    public string ConfigurationVersion { get; init; } = string.Empty;

    [JsonPropertyName("calibration")]
    public EvaluationPlanCorpus Calibration { get; init; } = new();

    [JsonPropertyName("holdout")]
    public EvaluationPlanCorpus Holdout { get; init; } = new();

    [JsonPropertyName("rubric")]
    public EvaluationFrozenFile Rubric { get; init; } = new();

    [JsonPropertyName("frozen_inputs")]
    public IReadOnlyList<EvaluationFrozenInput> FrozenInputs { get; init; } = [];

    [JsonPropertyName("provider")]
    public EvaluationProviderFreeze Provider { get; init; } = new();

    [JsonPropertyName("runner")]
    public EvaluationRunnerFreeze Runner { get; init; } = new();

    [JsonPropertyName("deadline_calibration")]
    public EvaluationDeadlineFreeze DeadlineCalibration { get; init; } = new();

    [JsonPropertyName("decision_analysis")]
    public EvaluationDecisionAnalysisFreeze DecisionAnalysis { get; init; } = new();

    [JsonPropertyName("calibration_readiness")]
    public EvaluationReadinessEvidence? CalibrationReadiness { get; init; }

    [JsonIgnore]
    public string RepositoryRoot { get; private set; } = string.Empty;

    public static EvaluationPlan Load(string path, string partition)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Evaluation plan path is required.", nameof(path));
        }

        RequirePartition(partition);
        var fullPath = Path.GetFullPath(path);
        var repositoryRoot = FindRepositoryRoot(Path.GetDirectoryName(fullPath)!);
        try
        {
            using var stream = File.OpenRead(fullPath);
            var plan = JsonSerializer.Deserialize<EvaluationPlan>(stream)
                ?? throw new InvalidDataException("Evaluation plan is empty.");
            plan.RepositoryRoot = repositoryRoot;
            plan.Validate(partition);
            return plan;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                or IOException
                or UnauthorizedAccessException
                or CryptographicException)
        {
            throw new InvalidDataException("Evaluation plan is malformed or unavailable.", exception);
        }
    }

    public EvaluationPlanSelection Select(string partition)
    {
        RequirePartition(partition);
        var corpus = partition == CalibrationPartition ? Calibration : Holdout;
        return new EvaluationPlanSelection(
            ResolveAndVerify(corpus.Path, corpus.Sha256, $"{partition} corpus"),
            ResolveAndVerify(Rubric.Path, Rubric.Sha256, "rubric"));
    }

    private void Validate(string partition)
    {
        if (PlanVersion != 1
            || !string.Equals(FixtureKind, ExpectedFixtureKind, StringComparison.Ordinal)
            || !string.Equals(FreezeStatus, ExpectedFreezeStatus, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(FreezeId)
            || string.IsNullOrWhiteSpace(CodeVersion)
            || string.IsNullOrWhiteSpace(ConfigurationVersion))
        {
            throw new InvalidDataException(
                "Evaluation plan version, fixture kind, freeze status, or version identifiers are invalid.");
        }

        if (!Holdout.SemanticOverlapReviewed)
        {
            throw new InvalidDataException(
                "The holdout requires an explicit completed semantic-overlap review.");
        }

        if (string.IsNullOrWhiteSpace(Provider.ProviderId)
            || Provider.ModelIds is null
            || Provider.ModelIds.Count == 0
            || Provider.ModelIds.Any(string.IsNullOrWhiteSpace)
            || Provider.ModelIds.Distinct(StringComparer.Ordinal).Count() != Provider.ModelIds.Count
            || string.IsNullOrWhiteSpace(Provider.PricingVersion)
            || string.IsNullOrWhiteSpace(Provider.OutputConstraintMode)
            || Provider.TimeoutSeconds <= 0
            || Runner.TimeoutSeconds <= Provider.TimeoutSeconds
            || Runner.PollMilliseconds <= 0)
        {
            throw new InvalidDataException("Evaluation provider or runner freeze is invalid.");
        }

        ValidateDeadline();
        ValidateDecisionAnalysis();
        ValidateFrozenInputs();

        var calibrationSelection = Select(CalibrationPartition);
        var holdoutSelection = Select(HoldoutPartition);
        var calibration = EvaluationCorpus.Load(calibrationSelection.CorpusPath);
        var holdout = EvaluationCorpus.Load(holdoutSelection.CorpusPath);
        var rubric = EvaluationRubric.Load(calibrationSelection.RubricPath);
        rubric.ValidateCorpus(calibration);
        rubric.ValidateCorpus(holdout);
        ValidateNoKnownOverlap(calibration, holdout);

        if (partition == HoldoutPartition)
        {
            ValidateCalibrationReadiness(calibration.Cases.Count);
        }
    }

    private void ValidateDeadline()
    {
        var selectedMilliseconds = checked(Provider.TimeoutSeconds * 1000);
        if (!string.Equals(
                DeadlineCalibration.Method,
                ExpectedDeadlineMethod,
                StringComparison.Ordinal)
            || DeadlineCalibration.SourceRunIds is null
            || DeadlineCalibration.SourceRunIds.Count == 0
            || DeadlineCalibration.SourceRunIds.Any(string.IsNullOrWhiteSpace)
            || DeadlineCalibration.ObservedUncensoredP95Milliseconds <= 0
            || DeadlineCalibration.RightCensoredCount < 0
            || DeadlineCalibration.CensoringBoundaryMilliseconds <= 0
            || DeadlineCalibration.OperationalMarginMilliseconds <= 0
            || DeadlineCalibration.SelectedTimeoutMilliseconds != selectedMilliseconds)
        {
            throw new InvalidDataException("Evaluation deadline calibration is invalid.");
        }

        var observedBoundary = DeadlineCalibration.RightCensoredCount > 0
            ? Math.Max(
                DeadlineCalibration.ObservedUncensoredP95Milliseconds,
                DeadlineCalibration.CensoringBoundaryMilliseconds)
            : DeadlineCalibration.ObservedUncensoredP95Milliseconds;
        if (DeadlineCalibration.SelectedTimeoutMilliseconds
            < observedBoundary + DeadlineCalibration.OperationalMarginMilliseconds)
        {
            throw new InvalidDataException(
                "The frozen provider timeout does not include the declared operational margin over the observed latency distribution.");
        }
    }

    private void ValidateDecisionAnalysis()
    {
        if (string.IsNullOrWhiteSpace(DecisionAnalysis.DimensionId)
            || string.IsNullOrWhiteSpace(DecisionAnalysis.NegativeLabel)
            || string.IsNullOrWhiteSpace(DecisionAnalysis.PositiveLabel)
            || string.Equals(
                DecisionAnalysis.NegativeLabel,
                DecisionAnalysis.PositiveLabel,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException("Evaluation decision-analysis freeze is invalid.");
        }
    }

    private void ValidateFrozenInputs()
    {
        var requiredRoles = new HashSet<string>(StringComparer.Ordinal)
        {
            "business-prompt",
            "organization-configuration",
            "provider-configuration",
            "evaluation-profile",
            "evaluation-runner-code",
        };
        var roles = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in FrozenInputs ?? [])
        {
            if (string.IsNullOrWhiteSpace(input.Role) || !roles.Add(input.Role))
            {
                throw new InvalidDataException("Frozen input roles must be non-empty and unique.");
            }

            _ = ResolveAndVerify(input.Path, input.Sha256, input.Role);
        }

        if (!requiredRoles.IsSubsetOf(roles))
        {
            throw new InvalidDataException(
                "Evaluation plan does not freeze every required prompt, configuration, and code input.");
        }
    }

    private void ValidateNoKnownOverlap(EvaluationCorpus calibration, EvaluationCorpus holdout)
    {
        var calibrationIds = calibration.Cases
            .Select(item => item.CaseId)
            .ToHashSet(StringComparer.Ordinal);
        var calibrationContexts = calibration.Cases
            .Select(item => NormalizeContext(item.Context))
            .ToHashSet(StringComparer.Ordinal);
        if (holdout.Cases.Any(item => calibrationIds.Contains(item.CaseId))
            || holdout.Cases.Any(item => calibrationContexts.Contains(NormalizeContext(item.Context))))
        {
            throw new InvalidDataException(
                "Calibration and holdout contain an overlapping case id or normalized context.");
        }
    }

    private void ValidateCalibrationReadiness(int expectedCaseCount)
    {
        if (CalibrationReadiness is null)
        {
            throw new InvalidDataException(
                "Holdout execution is locked until a frozen calibration readiness dataset is declared.");
        }

        var evidencePath = ResolveAndVerify(
            CalibrationReadiness.DatasetPath,
            CalibrationReadiness.Sha256,
            "calibration readiness evidence");
        using var document = JsonDocument.Parse(File.ReadAllText(evidencePath));
        var root = document.RootElement;
        if (RequiredString(root, "freeze_id") != FreezeId
            || RequiredString(root, "evaluation_partition") != CalibrationPartition
            || RequiredString(root.GetProperty("run_analysis"), "status") != "ready"
            || root.GetProperty("cases").GetArrayLength() != expectedCaseCount)
        {
            throw new InvalidDataException(
                "Calibration readiness evidence does not prove a complete ready run for this freeze.");
        }
    }

    private string ResolveAndVerify(string path, string expectedSha256, string role)
    {
        if (string.IsNullOrWhiteSpace(path)
            || string.IsNullOrWhiteSpace(expectedSha256)
            || expectedSha256.Length != 64
            || expectedSha256.Any(character =>
                !char.IsAsciiHexDigit(character) || char.IsAsciiLetterUpper(character)))
        {
            throw new InvalidDataException($"Frozen {role} path or SHA-256 is invalid.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            RepositoryRoot,
            path.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw new InvalidDataException($"Frozen {role} is missing or outside the repository.");
        }

        using var stream = File.OpenRead(fullPath);
        var actual = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(actual, expectedSha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"Frozen {role} has drifted from SHA-256 {expectedSha256}.");
        }

        return fullPath;
    }

    private static string NormalizeContext(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value.Trim())
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(character));
        }

        return builder.ToString();
    }

    private static string RequiredString(JsonElement element, string propertyName)
    {
        var value = element.GetProperty(propertyName).GetString();
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidDataException(
                $"Calibration readiness field '{propertyName}' is required.");
        }

        return value;
    }

    private static void RequirePartition(string partition)
    {
        if (partition is not CalibrationPartition and not HoldoutPartition)
        {
            throw new ArgumentException(
                "Evaluation partition must be 'calibration' or 'holdout'.",
                nameof(partition));
        }
    }

    private static string FindRepositoryRoot(string path)
    {
        var current = new DirectoryInfo(path);
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

public sealed class EvaluationPlanCorpus : EvaluationFrozenFile
{
    [JsonPropertyName("semantic_overlap_reviewed")]
    public bool SemanticOverlapReviewed { get; init; }
}

public class EvaluationFrozenFile
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class EvaluationFrozenInput : EvaluationFrozenFile
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;
}

public sealed class EvaluationProviderFreeze
{
    [JsonPropertyName("provider_id")]
    public string ProviderId { get; init; } = string.Empty;

    [JsonPropertyName("model_ids")]
    public IReadOnlyList<string> ModelIds { get; init; } = [];

    [JsonPropertyName("pricing_version")]
    public string PricingVersion { get; init; } = string.Empty;

    [JsonPropertyName("output_constraint_mode")]
    public string OutputConstraintMode { get; init; } = string.Empty;

    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; }
}

public sealed class EvaluationRunnerFreeze
{
    [JsonPropertyName("timeout_seconds")]
    public int TimeoutSeconds { get; init; }

    [JsonPropertyName("poll_milliseconds")]
    public int PollMilliseconds { get; init; }
}

public sealed class EvaluationDeadlineFreeze
{
    [JsonPropertyName("method")]
    public string Method { get; init; } = string.Empty;

    [JsonPropertyName("source_run_ids")]
    public IReadOnlyList<string> SourceRunIds { get; init; } = [];

    [JsonPropertyName("observed_uncensored_p95_ms")]
    public int ObservedUncensoredP95Milliseconds { get; init; }

    [JsonPropertyName("right_censored_count")]
    public int RightCensoredCount { get; init; }

    [JsonPropertyName("censoring_boundary_ms")]
    public int CensoringBoundaryMilliseconds { get; init; }

    [JsonPropertyName("operational_margin_ms")]
    public int OperationalMarginMilliseconds { get; init; }

    [JsonPropertyName("selected_timeout_ms")]
    public int SelectedTimeoutMilliseconds { get; init; }
}

public sealed class EvaluationDecisionAnalysisFreeze
{
    [JsonPropertyName("dimension_id")]
    public string DimensionId { get; init; } = string.Empty;

    [JsonPropertyName("negative_label")]
    public string NegativeLabel { get; init; } = string.Empty;

    [JsonPropertyName("positive_label")]
    public string PositiveLabel { get; init; } = string.Empty;
}

public sealed class EvaluationReadinessEvidence
{
    [JsonPropertyName("dataset_path")]
    public string DatasetPath { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed record EvaluationPlanSelection(string CorpusPath, string RubricPath);
