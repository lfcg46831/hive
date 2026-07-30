using System.Text.Json;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationArtifactPublicationTests
{
    [Fact]
    public async Task Publishes_exact_bytes_by_hash_and_writes_the_only_repository_index()
    {
        using var fixture = ArtifactPublicationFixture.Create();

        var publication = await EvaluationArtifactPublisher.PublishAsync(
            fixture.Options,
            CancellationToken.None);

        Assert.False(publication.WasAlreadyIndexed);
        Assert.Equal(
            File.ReadAllBytes(fixture.DatasetPath),
            File.ReadAllBytes(publication.ObjectPath));
        Assert.StartsWith(
            fixture.LocationBase.AbsoluteUri,
            publication.Entry.Location,
            StringComparison.Ordinal);
        Assert.Matches("^[0-9a-f]{64}$", publication.Entry.Sha256);
        Assert.Equal(1, publication.Entry.CaseCount);
        Assert.Equal("validated", publication.Entry.ConfigurationStatus);
        Assert.Equal(
            EvaluationArtifactIndex.IndexPath(fixture.RepositoryRoot),
            publication.IndexPath);

        var index = EvaluationArtifactIndex.Load(fixture.RepositoryRoot);
        var entry = Assert.Single(index.Artifacts);
        Assert.Equal(fixture.Manifest.ExperimentId, entry.ExperimentId);
        Assert.Equal("publication-test", entry.RunId);
        Assert.Equal(
            "config/experiments/bug-triage-lab-v1/experiment.v1.json",
            entry.Manifest.Path);
        Assert.Equal(
            "evidence/evaluation/bug-triage-lab-v1/publication-test-summary.md",
            entry.SummaryReport.Path);

        var repeated = await EvaluationArtifactPublisher.PublishAsync(
            fixture.Options,
            CancellationToken.None);

        Assert.True(repeated.WasAlreadyIndexed);
        Assert.Equal(publication.Entry, repeated.Entry);
        Assert.Single(EvaluationArtifactIndex.Load(
            fixture.RepositoryRoot).Artifacts);
    }

    [Fact]
    public async Task Reused_run_scope_fails_without_overwriting_published_bytes()
    {
        using var fixture = ArtifactPublicationFixture.Create();
        var first = await EvaluationArtifactPublisher.PublishAsync(
            fixture.Options,
            CancellationToken.None);
        var publishedBytes = File.ReadAllBytes(first.ObjectPath);
        fixture.WriteDataset(additionalMarker: "different-bytes");

        var exception = await Assert.ThrowsAsync<InvalidDataException>(() =>
            EvaluationArtifactPublisher.PublishAsync(
                fixture.Options,
                CancellationToken.None));

        Assert.Contains("already indexed", exception.Message, StringComparison.Ordinal);
        Assert.Equal(publishedBytes, File.ReadAllBytes(first.ObjectPath));
        Assert.Single(EvaluationArtifactIndex.Load(
            fixture.RepositoryRoot).Artifacts);
    }

    [Fact]
    public async Task Rejects_repository_raw_dataset_outside_disposable_artifacts()
    {
        using var fixture = ArtifactPublicationFixture.Create();
        var nonDisposableDataset = Path.Combine(
            fixture.RepositoryRoot,
            "evidence",
            "evaluation",
            "raw.json");
        File.Copy(fixture.DatasetPath, nonDisposableDataset);
        var options = fixture.Options with
        {
            DatasetPath = nonDisposableDataset,
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EvaluationArtifactPublisher.PublishAsync(
                options,
                CancellationToken.None));

        Assert.Contains(
            "disposable artifacts directory",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.ArtifactStore));
    }

    [Fact]
    public async Task Rejects_artifact_storage_inside_the_repository()
    {
        using var fixture = ArtifactPublicationFixture.Create();
        var repositoryStore = Path.Combine(
            fixture.RepositoryRoot,
            "artifacts",
            "external-store");
        var options = fixture.Options with
        {
            ArtifactStore = repositoryStore,
            LocationBase = new Uri(
                repositoryStore + Path.DirectorySeparatorChar,
                UriKind.Absolute),
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EvaluationArtifactPublisher.PublishAsync(
                options,
                CancellationToken.None));

        Assert.Contains(
            "outside the repository",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(repositoryStore));
    }

    [Theory]
    [InlineData("https://user:secret@artifacts.example/hive/evaluation/")]
    [InlineData("https://artifacts.example/hive/evaluation/?signature=secret")]
    public async Task Rejects_credential_bearing_artifact_locations(
        string locationBase)
    {
        using var fixture = ArtifactPublicationFixture.Create();
        var options = fixture.Options with
        {
            LocationBase = new Uri(locationBase),
        };

        var exception = await Assert.ThrowsAsync<ArgumentException>(() =>
            EvaluationArtifactPublisher.PublishAsync(
                options,
                CancellationToken.None));

        Assert.Contains(
            "credential-free URI",
            exception.Message,
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.ArtifactStore));
    }

    [Fact]
    public async Task Existing_index_fails_closed_when_a_repository_reference_drifts()
    {
        using var fixture = ArtifactPublicationFixture.Create();
        await EvaluationArtifactPublisher.PublishAsync(
            fixture.Options,
            CancellationToken.None);
        File.AppendAllText(fixture.SummaryReportPath, "drift");

        var exception = Assert.Throws<InvalidDataException>(() =>
            EvaluationArtifactIndex.Load(fixture.RepositoryRoot));

        Assert.Contains("has drifted", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Command_rejects_non_utc_or_non_increasing_retention()
    {
        using var fixture = ArtifactPublicationFixture.Create();
        using var output = new StringWriter();

        var exitCode = await EvaluationArtifactCommand.RunAsync(
            [
                "publish",
                "--repository-root", fixture.RepositoryRoot,
                "--dataset", fixture.DatasetPath,
                "--manifest", fixture.Manifest.ManifestPath,
                "--summary-report", fixture.SummaryReportPath,
                "--artifact-store", fixture.ArtifactStore,
                "--location-base", fixture.LocationBase.AbsoluteUri,
                "--published-at", "2026-07-30T12:00:00+01:00",
                "--retain-until", "2026-07-30T12:00:00Z",
            ],
            output,
            CancellationToken.None);

        Assert.Equal(2, exitCode);
        Assert.Contains(
            "explicit UTC timestamp",
            output.ToString(),
            StringComparison.Ordinal);
        Assert.False(Directory.Exists(fixture.ArtifactStore));
    }

    [Fact]
    public void Tracked_index_starts_as_a_valid_empty_v1_contract()
    {
        var index = EvaluationArtifactIndex.Load(RepositoryRoot);

        Assert.Equal(EvaluationArtifactIndex.ContractName, index.Name);
        Assert.Equal(EvaluationArtifactIndex.CurrentVersion, index.Version);
        Assert.Empty(index.Artifacts);
    }

    private sealed class ArtifactPublicationFixture : IDisposable
    {
        private readonly string _root;

        private ArtifactPublicationFixture(
            string root,
            string repositoryRoot,
            string artifactStore,
            string datasetPath,
            string summaryReportPath,
            Uri locationBase,
            EvaluationExperimentManifest manifest)
        {
            _root = root;
            RepositoryRoot = repositoryRoot;
            ArtifactStore = artifactStore;
            DatasetPath = datasetPath;
            SummaryReportPath = summaryReportPath;
            LocationBase = locationBase;
            Manifest = manifest;
            Options = new EvaluationArtifactPublicationOptions(
                repositoryRoot,
                datasetPath,
                manifest.ManifestPath,
                summaryReportPath,
                artifactStore,
                locationBase,
                DateTimeOffset.Parse("2026-07-30T12:00:00Z"),
                DateTimeOffset.Parse("2027-07-30T12:00:00Z"));
        }

        public string RepositoryRoot { get; }

        public string ArtifactStore { get; }

        public string DatasetPath { get; }

        public string SummaryReportPath { get; }

        public Uri LocationBase { get; }

        public EvaluationExperimentManifest Manifest { get; }

        public EvaluationArtifactPublicationOptions Options { get; }

        public static ArtifactPublicationFixture Create()
        {
            var root = Path.Combine(
                EvaluationArtifactPublicationTests.RepositoryRoot,
                "artifacts",
                "evaluation-tests",
                Guid.NewGuid().ToString("N"));
            var repositoryRoot = Path.Combine(root, "repository");
            var artifactStore = Path.Combine(root, "external-store");
            Directory.CreateDirectory(repositoryRoot);
            File.WriteAllText(Path.Combine(repositoryRoot, "Hive.sln"), string.Empty);

            var sourceManifestPath = Path.Combine(
                EvaluationArtifactPublicationTests.RepositoryRoot,
                "config",
                "experiments",
                "bug-triage-lab-v1",
                "experiment.v1.json");
            CopyManifestInputs(sourceManifestPath, repositoryRoot);
            var manifestPath = Path.Combine(
                repositoryRoot,
                "config",
                "experiments",
                "bug-triage-lab-v1",
                "experiment.v1.json");
            var manifest = EvaluationExperimentManifest.Load(manifestPath);

            var summaryReportPath = Path.Combine(
                repositoryRoot,
                "evidence",
                "evaluation",
                manifest.ExperimentId,
                "publication-test-summary.md");
            Directory.CreateDirectory(Path.GetDirectoryName(summaryReportPath)!);
            File.WriteAllText(
                summaryReportPath,
                "# Publication test summary\n");

            var datasetPath = Path.Combine(
                repositoryRoot,
                "artifacts",
                "evaluation",
                "publication-test.json");
            Directory.CreateDirectory(Path.GetDirectoryName(datasetPath)!);
            var locationBase = new Uri(
                artifactStore.TrimEnd(
                        Path.DirectorySeparatorChar,
                        Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar,
                UriKind.Absolute);
            var fixture = new ArtifactPublicationFixture(
                root,
                repositoryRoot,
                artifactStore,
                datasetPath,
                summaryReportPath,
                locationBase,
                manifest);
            fixture.WriteDataset();
            return fixture;
        }

        public void WriteDataset(string? additionalMarker = null)
        {
            var value = new
            {
                schema_version = 1,
                run_id = "publication-test",
                experiment_manifest_version = 1,
                experiment_id = Manifest.ExperimentId,
                experiment_manifest_sha256 = Manifest.ManifestSha256,
                effective_configuration_sha256 =
                    Manifest.EffectiveConfigurationSha256,
                effective_configuration_validation = new
                {
                    status = "validated",
                    failure_codes = Array.Empty<string>(),
                },
                cases = new[]
                {
                    new
                    {
                        case_id = "case-001",
                    },
                },
                marker = additionalMarker,
            };
            File.WriteAllText(
                DatasetPath,
                JsonSerializer.Serialize(value, new JsonSerializerOptions
                {
                    WriteIndented = true,
                }) + "\n");
        }

        public void Dispose()
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true);
            }
        }

        private static void CopyManifestInputs(
            string sourceManifestPath,
            string targetRepositoryRoot)
        {
            using var document = JsonDocument.Parse(
                File.ReadAllText(sourceManifestPath));
            var root = document.RootElement;
            var paths = new List<string>
            {
                "config/experiments/bug-triage-lab-v1/experiment.v1.json",
                root.GetProperty("organization")
                    .GetProperty("configuration")
                    .GetProperty("path")
                    .GetString()!,
                root.GetProperty("evaluation")
                    .GetProperty("corpus")
                    .GetProperty("path")
                    .GetString()!,
                root.GetProperty("evaluation")
                    .GetProperty("rubric")
                    .GetProperty("path")
                    .GetString()!,
            };
            paths.AddRange(root.GetProperty("reproducibility")
                .EnumerateArray()
                .Select(item => item.GetProperty("path").GetString()!));

            foreach (var relativePath in paths.Distinct(StringComparer.Ordinal))
            {
                var source = Path.Combine(
                    EvaluationArtifactPublicationTests.RepositoryRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                var target = Path.Combine(
                    targetRepositoryRoot,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(source, target);
            }
        }
    }

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

            throw new InvalidOperationException(
                "Could not locate the Hive repository root.");
        }
    }
}
