using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;

namespace Hive.Evaluation.Tooling.Evaluation;

public static class EvaluationArtifactPublisher
{
    private static readonly JsonSerializerOptions OutputJson = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<EvaluationArtifactPublication> PublishAsync(
        EvaluationArtifactPublicationOptions options,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.PublishedAt.Offset != TimeSpan.Zero
            || options.RetainUntil.Offset != TimeSpan.Zero
            || options.RetainUntil <= options.PublishedAt)
        {
            throw new ArgumentException(
                "Artifact publication requires explicit UTC timestamps with retention later than publication.");
        }

        var repositoryRoot = Path.GetFullPath(options.RepositoryRoot);
        var datasetPath = Path.GetFullPath(options.DatasetPath);
        var artifactStore = Path.GetFullPath(options.ArtifactStore);
        EnsurePublicationBoundaries(repositoryRoot, datasetPath, artifactStore);

        var manifest = EvaluationExperimentManifest.Load(options.ManifestPath);
        if (!PathsEqual(repositoryRoot, manifest.RepositoryRoot))
        {
            throw new ArgumentException(
                "The experiment manifest must belong to the selected repository.");
        }

        var summaryReport = RepositoryReference(
            repositoryRoot,
            options.SummaryReportPath,
            Path.Combine(repositoryRoot, "evidence", "evaluation"),
            "summary report");
        var manifestReference = RepositoryReference(
            repositoryRoot,
            manifest.ManifestPath,
            Path.Combine(repositoryRoot, "config", "experiments"),
            "manifest");
        var dataset = ReadDataset(datasetPath, manifest);
        var datasetHash = EvaluationArtifactIndex.FileSha256(datasetPath);
        var datasetSize = new FileInfo(datasetPath).Length;
        var relativeObjectPath = Path.Combine(
            "sha256",
            datasetHash[..2],
            $"{datasetHash}.json");
        var objectPath = Path.Combine(artifactStore, relativeObjectPath);
        var location = BuildLocation(
            options.LocationBase,
            artifactStore,
            relativeObjectPath);

        var entry = new EvaluationArtifactIndexEntry(
            manifest.ExperimentId,
            dataset.RunId,
            EvaluationArtifactIndexEntry.JsonMediaType,
            location,
            datasetHash,
            datasetSize,
            dataset.CaseCount,
            dataset.ConfigurationStatus,
            options.PublishedAt.ToUniversalTime(),
            options.RetainUntil.ToUniversalTime(),
            manifestReference,
            summaryReport);
        new EvaluationArtifactIndex
        {
            Artifacts = [entry],
        }.Validate(repositoryRoot);

        await PublishObjectAsync(
                datasetPath,
                objectPath,
                datasetHash,
                datasetSize,
                cancellationToken)
            .ConfigureAwait(false);

        var lockPath = Path.Combine(
            repositoryRoot,
            "artifacts",
            "evaluation",
            "artifact-index.v1.lock");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        await using var indexLock = new FileStream(
            lockPath,
            FileMode.OpenOrCreate,
            FileAccess.ReadWrite,
            FileShare.None,
            bufferSize: 1,
            FileOptions.Asynchronous);

        var index = EvaluationArtifactIndex.Load(repositoryRoot);
        var updated = index.Add(entry);
        updated.Validate(repositoryRoot);
        var indexPath = EvaluationArtifactIndex.IndexPath(repositoryRoot);
        if (!ReferenceEquals(index, updated))
        {
            await WriteIndexAsync(indexPath, updated, cancellationToken)
                .ConfigureAwait(false);
        }

        return new EvaluationArtifactPublication(
            objectPath,
            indexPath,
            entry,
            ReferenceEquals(index, updated));
    }

    private static void EnsurePublicationBoundaries(
        string repositoryRoot,
        string datasetPath,
        string artifactStore)
    {
        if (!File.Exists(datasetPath))
        {
            throw new ArgumentException(
                "Evaluation dataset is missing.",
                nameof(datasetPath));
        }

        if (EvaluationArtifactIndex.IsWithin(repositoryRoot, artifactStore))
        {
            throw new ArgumentException(
                "Evaluation artifact storage must be outside the repository.");
        }

        if (EvaluationArtifactIndex.IsWithin(repositoryRoot, datasetPath)
            && !EvaluationArtifactIndex.IsWithin(
                Path.Combine(repositoryRoot, "artifacts"),
                datasetPath))
        {
            throw new ArgumentException(
                "A repository-local raw dataset must stay under the disposable artifacts directory.");
        }
    }

    private static EvaluationArtifactDatasetIdentity ReadDataset(
        string path,
        EvaluationExperimentManifest manifest)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var document = JsonDocument.Parse(stream);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || RequiredInt(root, "schema_version") != 1
                || RequiredInt(root, "experiment_manifest_version")
                    != EvaluationExperimentManifest.CurrentVersion
                || !string.Equals(
                    RequiredString(root, "experiment_id"),
                    manifest.ExperimentId,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RequiredString(root, "experiment_manifest_sha256"),
                    manifest.ManifestSha256,
                    StringComparison.Ordinal)
                || !string.Equals(
                    RequiredString(root, "effective_configuration_sha256"),
                    manifest.EffectiveConfigurationSha256,
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Evaluation dataset is not bound to the selected experiment manifest.");
            }

            var runId = RequiredString(root, "run_id");
            var cases = root.GetProperty("cases");
            var validation = root
                .GetProperty("effective_configuration_validation");
            var configurationStatus = RequiredString(validation, "status");
            if (cases.ValueKind != JsonValueKind.Array
                || cases.GetArrayLength() == 0
                || configurationStatus is not ("validated" or "invalid"))
            {
                throw new InvalidDataException(
                    "Evaluation dataset cases or effective configuration validation are invalid.");
            }

            return new EvaluationArtifactDatasetIdentity(
                runId,
                cases.GetArrayLength(),
                configurationStatus);
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException
                or IOException
                or UnauthorizedAccessException
                or KeyNotFoundException
                or InvalidOperationException)
        {
            throw new InvalidDataException(
                "Evaluation dataset is malformed or unavailable.",
                exception);
        }
    }

    private static string RequiredString(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
        {
            throw new InvalidDataException(
                $"Evaluation dataset property '{name}' is invalid.");
        }

        return value.GetString()!;
    }

    private static int RequiredInt(JsonElement parent, string name)
    {
        var value = parent.GetProperty(name);
        if (value.ValueKind != JsonValueKind.Number
            || !value.TryGetInt32(out var parsed))
        {
            throw new InvalidDataException(
                $"Evaluation dataset property '{name}' is invalid.");
        }

        return parsed;
    }

    private static EvaluationArtifactRepositoryReference RepositoryReference(
        string repositoryRoot,
        string path,
        string requiredParent,
        string role)
    {
        var fullPath = Path.GetFullPath(path);
        if (!File.Exists(fullPath)
            || !EvaluationArtifactIndex.IsWithin(repositoryRoot, fullPath)
            || !EvaluationArtifactIndex.IsWithin(requiredParent, fullPath))
        {
            throw new ArgumentException(
                $"Evaluation artifact {role} must exist inside its repository boundary.");
        }

        var relativePath = Path.GetRelativePath(repositoryRoot, fullPath)
            .Replace(Path.DirectorySeparatorChar, '/');
        return new EvaluationArtifactRepositoryReference(
            relativePath,
            EvaluationArtifactIndex.FileSha256(fullPath));
    }

    private static string BuildLocation(
        Uri locationBase,
        string artifactStore,
        string relativeObjectPath)
    {
        ArgumentNullException.ThrowIfNull(locationBase);
        if (!locationBase.IsAbsoluteUri
            || !string.IsNullOrEmpty(locationBase.UserInfo)
            || !string.IsNullOrEmpty(locationBase.Query)
            || !string.IsNullOrEmpty(locationBase.Fragment))
        {
            throw new ArgumentException(
                "Artifact location base must be an absolute credential-free URI without query or fragment.");
        }

        if (locationBase.IsFile
            && !PathsEqual(locationBase.LocalPath, artifactStore))
        {
            throw new ArgumentException(
                "A file artifact location base must identify the selected artifact store.");
        }

        var baseValue = locationBase.AbsoluteUri.TrimEnd('/') + "/";
        var relativeValue = relativeObjectPath
            .Replace(Path.DirectorySeparatorChar, '/');
        return new Uri(new Uri(baseValue, UriKind.Absolute), relativeValue)
            .AbsoluteUri;
    }

    private static async Task PublishObjectAsync(
        string sourcePath,
        string objectPath,
        string expectedHash,
        long expectedSize,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(objectPath)!;
        Directory.CreateDirectory(directory);
        if (File.Exists(objectPath))
        {
            VerifyObject(objectPath, expectedHash, expectedSize);
            return;
        }

        var temporaryPath = $"{objectPath}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var source = new FileStream(
                sourcePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var destination = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(destination, cancellationToken)
                    .ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            VerifyObject(temporaryPath, expectedHash, expectedSize);
            try
            {
                File.Move(temporaryPath, objectPath);
            }
            catch (IOException) when (File.Exists(objectPath))
            {
                VerifyObject(objectPath, expectedHash, expectedSize);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static void VerifyObject(
        string path,
        string expectedHash,
        long expectedSize)
    {
        var info = new FileInfo(path);
        if (info.Length != expectedSize
            || !string.Equals(
                EvaluationArtifactIndex.FileSha256(path),
                expectedHash,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Content-addressed evaluation artifact already exists with different bytes.");
        }
    }

    private static async Task WriteIndexAsync(
        string path,
        EvaluationArtifactIndex index,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporaryPath = $"{path}.tmp-{Guid.NewGuid():N}";
        try
        {
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 81920,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        index,
                        OutputJson,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken)
                    .ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

    private sealed record EvaluationArtifactDatasetIdentity(
        string RunId,
        int CaseCount,
        string ConfigurationStatus);
}

public sealed record EvaluationArtifactPublicationOptions(
    string RepositoryRoot,
    string DatasetPath,
    string ManifestPath,
    string SummaryReportPath,
    string ArtifactStore,
    Uri LocationBase,
    DateTimeOffset PublishedAt,
    DateTimeOffset RetainUntil);

public sealed record EvaluationArtifactPublication(
    string ObjectPath,
    string IndexPath,
    EvaluationArtifactIndexEntry Entry,
    bool WasAlreadyIndexed);
