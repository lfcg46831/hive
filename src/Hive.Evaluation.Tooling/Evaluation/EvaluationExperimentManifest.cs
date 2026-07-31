using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hive.Evaluation.Tooling.Evaluation;

public sealed partial class EvaluationExperimentManifest
{
    public const string ContractName = "hive.evaluation-experiment";
    public const int CurrentVersion = 1;
    public const string PreparedStatus = "prepared";

    private const int MaxManifestBytes = 256 * 1024;
    private static readonly JsonSerializerOptions ManifestJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    private static readonly JsonSerializerOptions CanonicalJson = new(JsonSerializerDefaults.Web);

    [JsonPropertyName("contract_name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("manifest_version")]
    public int ManifestVersion { get; init; }

    [JsonPropertyName("experiment_id")]
    public string ExperimentId { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("organization")]
    public EvaluationExperimentOrganization Organization { get; init; } = new();

    [JsonPropertyName("directive")]
    public EvaluationExperimentDirective Directive { get; init; } = new();

    [JsonPropertyName("model")]
    public EvaluationExperimentModel Model { get; init; } = new();

    [JsonPropertyName("limits")]
    public EvaluationExperimentLimits Limits { get; init; } = new();

    [JsonPropertyName("policy")]
    public EvaluationExperimentPolicy Policy { get; init; } = new();

    [JsonPropertyName("evaluation")]
    public EvaluationExperimentInputs Evaluation { get; init; } = new();

    [JsonPropertyName("reproducibility")]
    public IReadOnlyList<EvaluationExperimentReproducibleInput> Reproducibility { get; init; } = [];

    [JsonIgnore]
    public string RepositoryRoot { get; private set; } = string.Empty;

    [JsonIgnore]
    public string ManifestPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string ManifestSha256 { get; private set; } = string.Empty;

    [JsonIgnore]
    public string OrganizationConfigurationPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string CorpusPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public string RubricPath { get; private set; } = string.Empty;

    [JsonIgnore]
    public EvaluationExperimentEffectiveConfiguration EffectiveConfiguration { get; private set; } =
        EvaluationExperimentEffectiveConfiguration.Empty;

    [JsonIgnore]
    public string EffectiveConfigurationSha256 { get; private set; } = string.Empty;

    public static EvaluationExperimentManifest Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var repositoryRoot = FindRepositoryRoot(Path.GetDirectoryName(fullPath)!);
        try
        {
            var bytes = File.ReadAllBytes(fullPath);
            if (bytes.Length == 0 || bytes.Length > MaxManifestBytes)
            {
                throw new InvalidDataException(
                    $"Experiment manifest must contain between 1 and {MaxManifestBytes} bytes.");
            }

            var manifest = JsonSerializer.Deserialize<EvaluationExperimentManifest>(
                    bytes,
                    ManifestJson)
                ?? throw new InvalidDataException("Experiment manifest is empty.");
            manifest.RepositoryRoot = repositoryRoot;
            manifest.ManifestPath = fullPath;
            manifest.ManifestSha256 = Sha256(bytes);
            manifest.ValidateAndResolve();
            return manifest;
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
            throw new InvalidDataException(
                "Experiment manifest is malformed or unavailable.",
                exception);
        }
    }

    public string RenderEnvironmentFile()
    {
        var variables = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["HIVE_EXPERIMENT_MODEL_ID"] = Model.ModelId,
            ["HIVE_EXPERIMENT_ORGANIZATION_ID"] = Organization.OrganizationId,
            ["HIVE_EXPERIMENT_ORGANIZATION_SOURCE"] =
                OrganizationConfigurationPath.Replace(Path.DirectorySeparatorChar, '/'),
            ["HIVE_EXPERIMENT_OUTCOME_MODE"] = Policy.OutcomeMode,
            ["HIVE_EXPERIMENT_POSITION_ID"] = Organization.PositionId,
            ["HIVE_EXPERIMENT_PROVIDER_ID"] = Model.ProviderId,
            ["HIVE_EXPERIMENT_LIMITS_VERSION"] = Limits.LimitsVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["HIVE_EXPERIMENT_EXECUTION_TIMEOUT"] =
                TimeSpan.FromMilliseconds(Limits.EffectiveExecutionTimeoutMilliseconds)
                    .ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            ["HIVE_EXPERIMENT_PER_CALL_TIMEOUT"] =
                TimeSpan.FromMilliseconds(Limits.ProviderTimeoutMilliseconds)
                    .ToString("c", System.Globalization.CultureInfo.InvariantCulture),
            ["HIVE_EXPERIMENT_VERIFIER_TIMEOUT"] =
                TimeSpan.FromMilliseconds(Limits.VerifierTimeoutMilliseconds)
                    .ToString("c", System.Globalization.CultureInfo.InvariantCulture),
        };
        var builder = new StringBuilder();
        builder.AppendLine("# Generated by Hive.Evaluation.Tooling. Contains no credentials.");
        builder.AppendLine($"# Experiment: {ExperimentId}");
        builder.AppendLine($"# Manifest SHA-256: {ManifestSha256}");
        foreach (var (name, value) in variables)
        {
            builder.Append(name).Append('=').AppendLine(value);
        }

        return builder.ToString().ReplaceLineEndings("\n");
    }

    public EvaluationExperimentPreparedConfiguration PreparedConfiguration() =>
        new(
            EvaluationExperimentPreparedConfiguration.ContractName,
            EvaluationExperimentPreparedConfiguration.ContractVersion,
            ManifestSha256,
            EffectiveConfigurationSha256,
            EffectiveConfiguration);

    private void ValidateAndResolve()
    {
        if (Organization is null
            || Directive is null
            || Model is null
            || Limits is null
            || Policy is null
            || Evaluation is null)
        {
            throw new InvalidDataException(
                "Experiment manifest sections are required.");
        }

        if (!string.Equals(Name, ContractName, StringComparison.Ordinal)
            || ManifestVersion != CurrentVersion
            || !string.Equals(Status, PreparedStatus, StringComparison.Ordinal)
            || !IsCanonicalId(ExperimentId))
        {
            throw new InvalidDataException(
                "Experiment manifest contract, version, id, or status is invalid.");
        }

        ValidateCanonicalId(Organization.OrganizationId, "organization id");
        ValidateCanonicalId(Organization.SourcePositionId, "source position id");
        ValidateCanonicalId(Organization.PositionId, "position id");
        if (Organization.SourcePositionId == Organization.PositionId)
        {
            throw new InvalidDataException(
                "Experiment source and destination positions must differ.");
        }

        ValidateText(Directive.Objective, "directive objective", 4096);
        if (Directive.CompletionCriteria is null
            || Directive.CompletionCriteria.Count == 0
            || Directive.CompletionCriteria.Count > 16
            || Directive.CompletionCriteria.Any(item =>
                string.IsNullOrWhiteSpace(item)
                || item.Length > 1024
                || item.Any(char.IsControl)))
        {
            throw new InvalidDataException(
                "Experiment directive requires between 1 and 16 bounded completion criteria.");
        }

        ValidateCanonicalId(Model.ProviderId, "provider id");
        ValidateText(Model.ModelId, "model id", 256);
        if (Model.OutputConstraintMode is not "json-schema" and not "json-object" and not "text")
        {
            throw new InvalidDataException(
                "Experiment output constraint mode is invalid.");
        }

        var executionTimeoutMilliseconds = Limits.EffectiveExecutionTimeoutMilliseconds;
        if (Limits.LimitsVersion is not 0 and not 1
            || (Limits.LimitsVersion == 0 && Limits.ExecutionTimeoutMilliseconds != 0)
            || (Limits.LimitsVersion == 1 && Limits.ExecutionTimeoutMilliseconds <= 0)
            || Limits.ProviderTimeoutMilliseconds <= 0
            || Limits.MaxOutputTokens <= 0
            || Limits.MaxIterations <= 0
            || Limits.VerifierTimeoutMilliseconds <= 0
            || Limits.RunnerTimeoutMilliseconds <= executionTimeoutMilliseconds
            || Limits.PollIntervalMilliseconds <= 0
            || Limits.PollIntervalMilliseconds >= Limits.RunnerTimeoutMilliseconds
            || Limits.RunnerTimeoutMilliseconds - executionTimeoutMilliseconds
                <= Limits.PollIntervalMilliseconds)
        {
            throw new InvalidDataException("Experiment limits are invalid.");
        }

        if (Policy.OutcomeMode is not "shadow" and not "enforcement")
        {
            throw new InvalidDataException("Experiment outcome mode is invalid.");
        }

        OrganizationConfigurationPath = ResolveAndVerify(
            Organization.Configuration,
            "organization configuration");
        CorpusPath = ResolveAndVerify(Evaluation.Corpus, "evaluation corpus");
        RubricPath = ResolveAndVerify(Evaluation.Rubric, "evaluation rubric");

        ValidateReproducibility();
        ValidateOrganizationConfiguration();
        EffectiveConfiguration = new EvaluationExperimentEffectiveConfiguration(
            ExperimentId,
            Organization.OrganizationId,
            Organization.SourcePositionId,
            Organization.PositionId,
            Organization.Configuration.Path,
            Organization.Configuration.Sha256,
            Model.ProviderId,
            Model.ModelId,
            Model.OutputConstraintMode,
            Limits.LimitsVersion,
            Limits.ProviderTimeoutMilliseconds,
            executionTimeoutMilliseconds,
            Limits.MaxOutputTokens,
            Limits.MaxIterations,
            Limits.VerifierTimeoutMilliseconds,
            Limits.RunnerTimeoutMilliseconds,
            Limits.PollIntervalMilliseconds,
            Policy.OutcomeMode,
            Evaluation.Corpus.Path,
            Evaluation.Corpus.Sha256,
            Evaluation.Rubric.Path,
            Evaluation.Rubric.Sha256);
        EffectiveConfigurationSha256 = Sha256(
            JsonSerializer.SerializeToUtf8Bytes(EffectiveConfiguration, CanonicalJson));
    }

    private void ValidateReproducibility()
    {
        var requiredRoles = new HashSet<string>(StringComparer.Ordinal)
        {
            "business-prompt",
            "experiment-compose",
            "evaluation-tooling",
        };
        var roles = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in Reproducibility ?? [])
        {
            if (input is null
                || !IsCanonicalId(input.Role)
                || !roles.Add(input.Role)
                || !paths.Add(input.Path))
            {
                throw new InvalidDataException(
                    "Experiment reproducibility roles and paths must be canonical and unique.");
            }

            _ = ResolveAndVerify(input, input.Role);
        }

        if (!requiredRoles.IsSubsetOf(roles))
        {
            throw new InvalidDataException(
                "Experiment reproducibility inputs are incomplete.");
        }
    }

    private void ValidateOrganizationConfiguration()
    {
        try
        {
            var snapshot = EvaluationOrganizationSnapshotReader.Read(
                OrganizationConfigurationPath);
            RequireEqual(
                snapshot.OrganizationId,
                Organization.OrganizationId,
                "organization id");
            _ = snapshot.Position(Organization.SourcePositionId);
            var target = snapshot.Position(Organization.PositionId);
            RequireEqual(
                target.ReportsTo,
                Organization.SourcePositionId,
                "position hierarchy");
            RequireEqual(
                target.OccupantType,
                "ai-agent",
                "occupant type");
            RequireEqual(target.ProviderId, Model.ProviderId, "provider");
            RequireEqual(target.ModelId, Model.ModelId, "model");
            RequireEqual(
                target.MaxOutputTokens,
                Limits.MaxOutputTokens,
                "max output tokens");
            RequireEqual(
                target.MaxIterations,
                Limits.MaxIterations,
                "max iterations");
            RequireEqual(
                target.LimitsVersion,
                Limits.LimitsVersion,
                "execution limits version");
            RequireEqual(
                target.ProviderTimeoutMilliseconds,
                Limits.ProviderTimeoutMilliseconds,
                "provider timeout");
            RequireEqual(
                target.ExecutionTimeoutMilliseconds,
                Limits.EffectiveExecutionTimeoutMilliseconds,
                "execution timeout");
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is IOException
                or UnauthorizedAccessException
                or FormatException
                or OverflowException)
        {
            throw new InvalidDataException(
                "Experiment organization configuration is malformed or unavailable.",
                exception);
        }
    }

    private string ResolveAndVerify(
        EvaluationExperimentFileReference reference,
        string role)
    {
        if (reference is null
            || string.IsNullOrWhiteSpace(reference.Path)
            || reference.Path.Any(char.IsControl)
            || Path.IsPathRooted(reference.Path)
            || !ValidSha256(reference.Sha256))
        {
            throw new InvalidDataException(
                $"Experiment {role} path or SHA-256 is invalid.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            RepositoryRoot,
            reference.Path.Replace('/', Path.DirectorySeparatorChar)));
        var rootPrefix = RepositoryRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)
            || !File.Exists(fullPath))
        {
            throw new InvalidDataException(
                $"Experiment {role} is missing or outside the repository.");
        }

        var actual = Sha256(File.ReadAllBytes(fullPath));
        if (!string.Equals(actual, reference.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Experiment {role} has drifted from SHA-256 {reference.Sha256}.");
        }

        return fullPath;
    }

    private static void RequireEqual<T>(T actual, T expected, string field)
    {
        if (!EqualityComparer<T>.Default.Equals(actual, expected))
        {
            throw new InvalidDataException(
                $"Experiment {field} differs from the organization snapshot.");
        }
    }

    private static void ValidateCanonicalId(string? value, string field)
    {
        if (!IsCanonicalId(value))
        {
            throw new InvalidDataException($"Experiment {field} is invalid.");
        }
    }

    private static void ValidateText(string? value, string field, int maximumLength)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value.Length > maximumLength
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsControl))
        {
            throw new InvalidDataException($"Experiment {field} is invalid.");
        }
    }

    private static bool IsCanonicalId(string? value) =>
        value is not null && CanonicalIdPattern().IsMatch(value);

    private static bool ValidSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static string Sha256(ReadOnlySpan<byte> bytes) =>
        Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

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

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalIdPattern();
}

public sealed class EvaluationExperimentOrganization
{
    [JsonPropertyName("organization_id")]
    public string OrganizationId { get; init; } = string.Empty;

    [JsonPropertyName("source_position_id")]
    public string SourcePositionId { get; init; } = string.Empty;

    [JsonPropertyName("position_id")]
    public string PositionId { get; init; } = string.Empty;

    [JsonPropertyName("configuration")]
    public EvaluationExperimentFileReference Configuration { get; init; } = new();
}

public sealed class EvaluationExperimentDirective
{
    [JsonPropertyName("objective")]
    public string Objective { get; init; } = string.Empty;

    [JsonPropertyName("completion_criteria")]
    public IReadOnlyList<string> CompletionCriteria { get; init; } = [];
}

public sealed class EvaluationExperimentModel
{
    [JsonPropertyName("provider_id")]
    public string ProviderId { get; init; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("output_constraint_mode")]
    public string OutputConstraintMode { get; init; } = string.Empty;
}

public sealed class EvaluationExperimentLimits
{
    [JsonPropertyName("limits_version")]
    public int LimitsVersion { get; init; }

    [JsonPropertyName("provider_timeout_ms")]
    public int ProviderTimeoutMilliseconds { get; init; }

    [JsonPropertyName("execution_timeout_ms")]
    public int ExecutionTimeoutMilliseconds { get; init; }

    [JsonIgnore]
    public int EffectiveExecutionTimeoutMilliseconds =>
        LimitsVersion == 0
            ? ProviderTimeoutMilliseconds
            : ExecutionTimeoutMilliseconds;

    [JsonPropertyName("max_output_tokens")]
    public int MaxOutputTokens { get; init; }

    [JsonPropertyName("max_iterations")]
    public int MaxIterations { get; init; }

    [JsonPropertyName("verifier_timeout_ms")]
    public int VerifierTimeoutMilliseconds { get; init; }

    [JsonPropertyName("runner_timeout_ms")]
    public int RunnerTimeoutMilliseconds { get; init; }

    [JsonPropertyName("poll_interval_ms")]
    public int PollIntervalMilliseconds { get; init; }
}

public sealed class EvaluationExperimentPolicy
{
    [JsonPropertyName("outcome_mode")]
    public string OutcomeMode { get; init; } = string.Empty;
}

public sealed class EvaluationExperimentInputs
{
    [JsonPropertyName("corpus")]
    public EvaluationExperimentFileReference Corpus { get; init; } = new();

    [JsonPropertyName("rubric")]
    public EvaluationExperimentFileReference Rubric { get; init; } = new();
}

public class EvaluationExperimentFileReference
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("sha256")]
    public string Sha256 { get; init; } = string.Empty;
}

public sealed class EvaluationExperimentReproducibleInput :
    EvaluationExperimentFileReference
{
    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;
}

public sealed record EvaluationExperimentEffectiveConfiguration(
    [property: JsonPropertyName("experiment_id")] string ExperimentId,
    [property: JsonPropertyName("organization_id")] string OrganizationId,
    [property: JsonPropertyName("source_position_id")] string SourcePositionId,
    [property: JsonPropertyName("position_id")] string PositionId,
    [property: JsonPropertyName("organization_configuration_path")]
    string OrganizationConfigurationPath,
    [property: JsonPropertyName("organization_configuration_sha256")]
    string OrganizationConfigurationSha256,
    [property: JsonPropertyName("provider_id")] string ProviderId,
    [property: JsonPropertyName("model_id")] string ModelId,
    [property: JsonPropertyName("output_constraint_mode")] string OutputConstraintMode,
    [property: JsonPropertyName("limits_version")] int LimitsVersion,
    [property: JsonPropertyName("provider_timeout_ms")] int ProviderTimeoutMilliseconds,
    [property: JsonPropertyName("execution_timeout_ms")] int ExecutionTimeoutMilliseconds,
    [property: JsonPropertyName("max_output_tokens")] int MaxOutputTokens,
    [property: JsonPropertyName("max_iterations")] int MaxIterations,
    [property: JsonPropertyName("verifier_timeout_ms")] int VerifierTimeoutMilliseconds,
    [property: JsonPropertyName("runner_timeout_ms")] int RunnerTimeoutMilliseconds,
    [property: JsonPropertyName("poll_interval_ms")] int PollIntervalMilliseconds,
    [property: JsonPropertyName("outcome_mode")] string OutcomeMode,
    [property: JsonPropertyName("corpus_path")] string CorpusPath,
    [property: JsonPropertyName("corpus_sha256")] string CorpusSha256,
    [property: JsonPropertyName("rubric_path")] string RubricPath,
    [property: JsonPropertyName("rubric_sha256")] string RubricSha256)
{
    internal static EvaluationExperimentEffectiveConfiguration Empty { get; } =
        new(
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            0,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty);
}

public sealed record EvaluationExperimentPreparedConfiguration(
    [property: JsonPropertyName("contract_name")] string Name,
    [property: JsonPropertyName("contract_version")] int Version,
    [property: JsonPropertyName("manifest_sha256")] string ManifestSha256,
    [property: JsonPropertyName("effective_configuration_sha256")]
    string EffectiveConfigurationSha256,
    [property: JsonPropertyName("configuration")]
    EvaluationExperimentEffectiveConfiguration Configuration)
{
    public const string ContractName = "hive.evaluation-effective-configuration";
    public const int ContractVersion = 2;
}
