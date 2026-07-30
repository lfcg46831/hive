using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Hive.Evaluation.Tooling.Evaluation;

public sealed partial class EvaluationArtifactIndex
{
    public const string ContractName = "hive.evaluation-artifact-index";
    public const int CurrentVersion = 1;
    public const string RepositoryPath = "evidence/evaluation/artifact-index.v1.json";

    private const int MaxIndexBytes = 4 * 1024 * 1024;
    private const int MaxEntries = 10_000;
    private static readonly JsonSerializerOptions InputJson = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    [JsonPropertyName("contract_name")]
    public string Name { get; init; } = ContractName;

    [JsonPropertyName("index_version")]
    public int Version { get; init; } = CurrentVersion;

    [JsonPropertyName("artifacts")]
    public IReadOnlyList<EvaluationArtifactIndexEntry> Artifacts { get; init; } = [];

    public static EvaluationArtifactIndex Empty { get; } = new();

    public static EvaluationArtifactIndex Load(string repositoryRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryRoot);
        var path = IndexPath(repositoryRoot);
        if (!File.Exists(path))
        {
            return Empty;
        }

        try
        {
            var bytes = File.ReadAllBytes(path);
            if (bytes.Length == 0 || bytes.Length > MaxIndexBytes)
            {
                throw new InvalidDataException(
                    $"Evaluation artifact index must contain between 1 and {MaxIndexBytes} bytes.");
            }

            var index = JsonSerializer.Deserialize<EvaluationArtifactIndex>(
                    bytes,
                    InputJson)
                ?? throw new InvalidDataException(
                    "Evaluation artifact index is empty.");
            index.Validate(repositoryRoot);
            return index;
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
                "Evaluation artifact index is malformed or unavailable.",
                exception);
        }
    }

    public EvaluationArtifactIndex Add(EvaluationArtifactIndexEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var existing = Artifacts.SingleOrDefault(item =>
            string.Equals(
                item.ExperimentId,
                entry.ExperimentId,
                StringComparison.Ordinal)
            && string.Equals(item.RunId, entry.RunId, StringComparison.Ordinal));
        if (existing is not null)
        {
            if (existing == entry)
            {
                return this;
            }

            throw new InvalidDataException(
                $"Evaluation artifact scope '{entry.ExperimentId}/{entry.RunId}' is already indexed with different metadata.");
        }

        return new EvaluationArtifactIndex
        {
            Artifacts = Artifacts
                .Append(entry)
                .OrderBy(item => item.ExperimentId, StringComparer.Ordinal)
                .ThenBy(item => item.RunId, StringComparer.Ordinal)
                .ToArray(),
        };
    }

    public void Validate(string repositoryRoot)
    {
        var fullRoot = Path.GetFullPath(repositoryRoot);
        if (!string.Equals(Name, ContractName, StringComparison.Ordinal)
            || Version != CurrentVersion
            || Artifacts is null
            || Artifacts.Count > MaxEntries
            || Artifacts.Any(item => item is null))
        {
            throw new InvalidDataException(
                "Evaluation artifact index contract, version, or entries are invalid.");
        }

        var scopes = new HashSet<string>(StringComparer.Ordinal);
        var locations = new HashSet<string>(StringComparer.Ordinal);
        EvaluationArtifactIndexEntry? previous = null;
        foreach (var entry in Artifacts)
        {
            ValidateEntry(entry, fullRoot);
            var scope = $"{entry.ExperimentId}/{entry.RunId}";
            if (!scopes.Add(scope)
                || !locations.Add(entry.Location)
                || (previous is not null && CompareScope(previous, entry) >= 0))
            {
                throw new InvalidDataException(
                    "Evaluation artifact index entries must have unique scopes and locations in canonical order.");
            }

            previous = entry;
        }
    }

    internal static string IndexPath(string repositoryRoot) => Path.Combine(
        Path.GetFullPath(repositoryRoot),
        RepositoryPath.Replace('/', Path.DirectorySeparatorChar));

    internal static string FileSha256(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    internal static bool IsWithin(string parent, string candidate)
    {
        var fullParent = Path.GetFullPath(parent);
        var fullCandidate = Path.GetFullPath(candidate);
        if (string.Equals(
                fullParent,
                fullCandidate,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var prefix = fullParent.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(
            prefix,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void ValidateEntry(
        EvaluationArtifactIndexEntry entry,
        string repositoryRoot)
    {
        if (!CanonicalIdPattern().IsMatch(entry.ExperimentId ?? string.Empty)
            || !CanonicalIdPattern().IsMatch(entry.RunId ?? string.Empty)
            || !string.Equals(
                entry.MediaType,
                EvaluationArtifactIndexEntry.JsonMediaType,
                StringComparison.Ordinal)
            || !ValidSha256(entry.Sha256)
            || entry.SizeBytes <= 0
            || entry.CaseCount <= 0
            || entry.ConfigurationStatus is not ("validated" or "invalid")
            || entry.PublishedAt.Offset != TimeSpan.Zero
            || entry.RetainUntil.Offset != TimeSpan.Zero
            || entry.RetainUntil <= entry.PublishedAt)
        {
            throw new InvalidDataException(
                "Evaluation artifact index entry metadata is invalid.");
        }

        if (!Uri.TryCreate(entry.Location, UriKind.Absolute, out var location)
            || !string.IsNullOrEmpty(location.UserInfo)
            || !string.IsNullOrEmpty(location.Query)
            || !string.IsNullOrEmpty(location.Fragment)
            || !location.AbsolutePath.EndsWith(
                $"/sha256/{entry.Sha256[..2]}/{entry.Sha256}.json",
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Evaluation artifact location is invalid or is not content-addressed.");
        }

        ValidateReference(
            entry.Manifest,
            repositoryRoot,
            Path.Combine(repositoryRoot, "config", "experiments"),
            "manifest");
        ValidateReference(
            entry.SummaryReport,
            repositoryRoot,
            Path.Combine(repositoryRoot, "evidence", "evaluation"),
            "summary report");
    }

    private static void ValidateReference(
        EvaluationArtifactRepositoryReference reference,
        string repositoryRoot,
        string requiredParent,
        string role)
    {
        if (reference is null
            || string.IsNullOrWhiteSpace(reference.Path)
            || reference.Path.Any(char.IsControl)
            || reference.Path.Contains('\\', StringComparison.Ordinal)
            || Path.IsPathRooted(reference.Path)
            || !ValidSha256(reference.Sha256))
        {
            throw new InvalidDataException(
                $"Evaluation artifact {role} reference is invalid.");
        }

        var fullPath = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            reference.Path.Replace('/', Path.DirectorySeparatorChar)));
        var canonicalPath = Path.GetRelativePath(repositoryRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        if (!string.Equals(canonicalPath, reference.Path, StringComparison.Ordinal)
            || !IsWithin(repositoryRoot, fullPath)
            || !IsWithin(requiredParent, fullPath)
            || !File.Exists(fullPath)
            || !string.Equals(
                FileSha256(fullPath),
                reference.Sha256,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Evaluation artifact {role} is missing, outside its repository boundary, or has drifted.");
        }
    }

    private static bool ValidSha256(string? value) =>
        value is not null
        && value.Length == 64
        && value.All(character =>
            char.IsAsciiHexDigit(character) && !char.IsAsciiLetterUpper(character));

    private static int CompareScope(
        EvaluationArtifactIndexEntry left,
        EvaluationArtifactIndexEntry right)
    {
        var experiment = string.CompareOrdinal(
            left.ExperimentId,
            right.ExperimentId);
        return experiment != 0
            ? experiment
            : string.CompareOrdinal(left.RunId, right.RunId);
    }

    [GeneratedRegex(
        "^[a-z0-9]+(?:-[a-z0-9]+)*$",
        RegexOptions.CultureInvariant)]
    private static partial Regex CanonicalIdPattern();
}

public sealed record EvaluationArtifactIndexEntry(
    [property: JsonPropertyName("experiment_id")] string ExperimentId,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("media_type")] string MediaType,
    [property: JsonPropertyName("location")] string Location,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size_bytes")] long SizeBytes,
    [property: JsonPropertyName("case_count")] int CaseCount,
    [property: JsonPropertyName("configuration_status")]
    string ConfigurationStatus,
    [property: JsonPropertyName("published_at")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("retain_until")] DateTimeOffset RetainUntil,
    [property: JsonPropertyName("manifest")]
    EvaluationArtifactRepositoryReference Manifest,
    [property: JsonPropertyName("summary_report")]
    EvaluationArtifactRepositoryReference SummaryReport)
{
    public const string JsonMediaType = "application/json";
}

public sealed record EvaluationArtifactRepositoryReference(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("sha256")] string Sha256);
