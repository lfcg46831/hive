using System.Text.Json;
using System.Net;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationExperimentManifestTests
{
    [Fact]
    public void Bug005_manifest_resolves_versioned_execution_limits_and_configuration()
    {
        using var fixture = CurrentManifestFixture.Create();
        var manifest = fixture.Manifest;

        Assert.Equal(EvaluationExperimentManifest.ContractName, manifest.Name);
        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal("bug-triage-lab-v2", manifest.ExperimentId);
        Assert.Equal("prepared", manifest.Status);
        Assert.Equal("acme-delivery", manifest.Organization.OrganizationId);
        Assert.Equal("delivery-lead", manifest.Organization.SourcePositionId);
        Assert.Equal("bug-triage", manifest.Organization.PositionId);
        Assert.Equal("openai", manifest.Model.ProviderId);
        Assert.Equal("gpt-5-mini-2025-08-07", manifest.Model.ModelId);
        Assert.Equal(1, manifest.Limits.LimitsVersion);
        Assert.Equal(60_000, manifest.Limits.ProviderTimeoutMilliseconds);
        Assert.Equal(90_000, manifest.Limits.ExecutionTimeoutMilliseconds);
        Assert.Equal(8_192, manifest.Limits.MaxOutputTokens);
        Assert.Equal(30_000, manifest.Limits.VerifierTimeoutMilliseconds);
        Assert.Matches("^[0-9a-f]{64}$", manifest.ManifestSha256);
        Assert.Matches("^[0-9a-f]{64}$", manifest.EffectiveConfigurationSha256);

        var environment = manifest.RenderEnvironmentFile();
        Assert.Contains(
            "HIVE_EXPERIMENT_ORGANIZATION_ID=acme-delivery",
            environment,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_VERIFIER_TIMEOUT=00:00:30",
            environment,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_EXECUTION_TIMEOUT=00:01:30",
            environment,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_PER_CALL_TIMEOUT=00:01:00",
            environment,
            StringComparison.Ordinal);
        Assert.DoesNotContain("APIKEY", environment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSWORD", environment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", environment, StringComparison.OrdinalIgnoreCase);

        var prepared = manifest.PreparedConfiguration();
        Assert.Equal(
            EvaluationExperimentPreparedConfiguration.ContractName,
            prepared.Name);
        Assert.Equal(2, prepared.Version);
        Assert.Equal(manifest.ManifestSha256, prepared.ManifestSha256);
        Assert.Equal(
            manifest.EffectiveConfigurationSha256,
            prepared.EffectiveConfigurationSha256);
        Assert.Equal(
            "config/experiments/bug-triage-lab-v2/organization.yaml",
            prepared.Configuration.OrganizationConfigurationPath);
    }

    [Fact]
    public void Historical_manifest_remains_byte_identical()
    {
        var historicalPath = Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "bug-triage-lab-v1",
            "experiment.v1.json");

        Assert.Equal(
            "e3ee9c5911129395b34fd68611088206eaa33bfa56c94e9a6264793c6357697d",
            EvaluationArtifactIndex.FileSha256(historicalPath));
    }

    [Fact]
    public void Legacy_manifest_and_dataset_without_split_metadata_remain_compatible()
    {
        using var fixture = CurrentManifestFixture.Create(HistoricalManifestPath);
        var manifest = fixture.Manifest;
        var legacyCall = GatewayCall(
            1,
            "directive-inference",
            1,
            "openai",
            "gpt-5-mini-2025-08-07",
            60_000,
            8_192) with
        {
            ExecutionLimitsVersion = null,
            ExecutionBudgetMilliseconds = null,
            PerCallTimeoutMilliseconds = null,
        };

        var validation = EvaluationExperimentValidator.Validate(
            [Result([legacyCall], "enforcement")],
            manifest);

        Assert.Equal(0, manifest.Limits.LimitsVersion);
        Assert.Equal(60_000, manifest.Limits.EffectiveExecutionTimeoutMilliseconds);
        Assert.Equal("validated", validation.Status);
        Assert.Empty(validation.FailureCodes);
    }

    [Fact]
    public void Manifest_rejects_unknown_properties_and_hash_drift()
    {
        using var fixture = CurrentManifestFixture.Create();
        var original = File.ReadAllText(fixture.Manifest.ManifestPath);
        WithManifestCopy(
            original.TrimEnd()[..^1] + ",\"unexpected\":true}",
            path =>
            {
                var exception = Assert.Throws<InvalidDataException>(
                    () => EvaluationExperimentManifest.Load(path));
                Assert.Equal(
                    "Experiment manifest is malformed or unavailable.",
                    exception.Message);
            });

        WithManifestCopy(
            original.Replace(
                fixture.Manifest.Organization.Configuration.Sha256,
                new string('0', 64),
                StringComparison.Ordinal),
            path =>
            {
                var exception = Assert.Throws<InvalidDataException>(
                    () => EvaluationExperimentManifest.Load(path));
                Assert.Contains(
                    "organization configuration has drifted",
                    exception.Message,
                    StringComparison.Ordinal);
            });

        WithManifestCopy(
            original.Replace(
                "\"execution_timeout_ms\": 90000",
                "\"execution_timeout_ms\": 0",
                StringComparison.Ordinal),
            path =>
            {
                var exception = Assert.Throws<InvalidDataException>(
                    () => EvaluationExperimentManifest.Load(path));
                Assert.Equal("Experiment limits are invalid.", exception.Message);
            });

        WithManifestCopy(
            original.Replace(
                "\"runner_timeout_ms\": 120000",
                "\"runner_timeout_ms\": 91000",
                StringComparison.Ordinal),
            path =>
            {
                var exception = Assert.Throws<InvalidDataException>(
                    () => EvaluationExperimentManifest.Load(path));
                Assert.Equal("Experiment limits are invalid.", exception.Message);
            });
    }

    [Fact]
    public void Manifest_drives_run_scope_and_disallows_runtime_overrides()
    {
        using var fixture = CurrentManifestFixture.Create();
        var options = EvaluationRunOptions.Parse(
            [
                "--run-id", "manifest-test",
                "--manifest", fixture.Manifest.ManifestPath,
            ],
            AppContext.BaseDirectory);

        Assert.NotNull(options.Experiment);
        Assert.Equal(TimeSpan.FromMinutes(2), options.Timeout);
        Assert.Equal(TimeSpan.FromSeconds(1), options.PollInterval);
        Assert.EndsWith(
            "bug-triage-corpus.v1.json",
            options.CorpusPath,
            StringComparison.Ordinal);

        var exception = Assert.Throws<ArgumentException>(() =>
            EvaluationRunOptions.Parse(
                [
                    "--run-id", "manifest-test",
                    "--manifest", fixture.Manifest.ManifestPath,
                    "--timeout-seconds", "10",
                ],
                AppContext.BaseDirectory));
        Assert.Contains(
            "cannot override an experiment manifest",
            exception.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Validator_accepts_bounded_runtime_and_reports_closed_drift_codes()
    {
        using var fixture = CurrentManifestFixture.Create();
        var manifest = fixture.Manifest;
        var valid = Result(
            [
                GatewayCall(
                    1,
                    "directive-inference",
                    1,
                    "openai",
                    "gpt-5-mini-2025-08-07",
                    60_000,
                    8_192),
                GatewayCall(
                    2,
                    "directive-inference",
                    2,
                    "openai",
                    "gpt-5-mini-2025-08-07",
                    45_000,
                    8_192),
                GatewayCall(
                    3,
                    "outcome-verification",
                    2,
                    "openai",
                    "gpt-5-mini-2025-08-07",
                    30_000,
                    2_048),
            ],
            "enforcement");

        var validated = EvaluationExperimentValidator.Validate([valid], manifest);

        Assert.Equal("validated", validated.Status);
        Assert.Empty(validated.FailureCodes);

        var driftedCall = GatewayCall(
                    1,
                    "directive-inference",
                    1,
                    "other",
                    "other-model",
                    61_000,
                    4_096) with
                {
                    ExecutionLimitsVersion = 2,
                    ExecutionBudgetMilliseconds = 91_000,
                    PerCallTimeoutMilliseconds = 61_000,
                };
        var drifted = Result(
            [
                driftedCall,
                GatewayCall(
                    2,
                    "outcome-verification",
                    1,
                    "other",
                    "other-model",
                    31_000,
                    2_048),
            ],
            "shadow");
        var invalid = EvaluationExperimentValidator.Validate([drifted], manifest);

        Assert.Equal("invalid", invalid.Status);
        Assert.Equal(
            [
                "execution-budget-drift",
                "execution-limits-version-drift",
                "max-output-tokens-drift",
                "model-drift",
                "outcome-mode-drift",
                "per-call-timeout-drift",
                "provider-drift",
                "provider-timeout-drift",
                "provider-timeout-expanded",
                "verifier-timeout-drift",
            ],
            invalid.FailureCodes);
    }

    [Fact]
    public async Task Runner_carries_manifest_scope_hashes_and_validation_into_dataset()
    {
        using var fixture = CurrentManifestFixture.Create();
        var manifest = fixture.Manifest;
        var rubric = EvaluationRubric.Load(manifest.RubricPath);
        var resolution = Resolution("enforcement");
        var calls = new[]
        {
            GatewayCall(
                1,
                "directive-inference",
                1,
                "openai",
                "gpt-5-mini-2025-08-07",
                60_000,
                8_192),
        };
        var journey = new EvaluationJourney(
            "succeeded",
            "result-emitted",
            "report",
            "openai",
            "gpt-5-mini-2025-08-07",
            "json-schema",
            1,
            1,
            2,
            false,
            0.01m,
            "USD",
            true,
            100,
            110,
            "estimated",
            "pricing-v1",
            1_000_000,
            0.25m,
            2m,
            OutcomeResolution: resolution,
            GatewayCalls: calls,
            OutcomeResolutionSteps: [resolution]);
        var audit = new SingleJourneyReader(journey);
        var handler = new AcceptedHandler();
        using var client = new HttpClient(handler);
        var options = new EvaluationRunOptions(
            RepositoryRoot,
            "manifest-run",
            new Uri("http://localhost:8080"),
            manifest.CorpusPath,
            "output.json",
            TimeSpan.FromMinutes(2),
            TimeSpan.FromSeconds(1),
            EvaluationRunOptions.DefaultSentAt,
            manifest.RubricPath,
            Experiment: manifest);
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [
                new EvaluationCase(
                    "case-001",
                    "test",
                    "A bounded synthetic context used to verify manifest-driven submission.",
                    CompleteReference()),
            ]);
        var projection = CompletePrediction();

        var dataset = await new EvaluationRunner(
                client,
                audit,
                projectionReader: new SingleProjectionReader(projection),
                rubric: rubric)
            .RunAsync(corpus, options, CancellationToken.None);

        Assert.Equal(1, dataset.ExperimentManifestVersion);
        Assert.Equal(manifest.ExperimentId, dataset.ExperimentId);
        Assert.Equal(manifest.ManifestSha256, dataset.ExperimentManifestSha256);
        Assert.Equal(
            manifest.EffectiveConfigurationSha256,
            dataset.EffectiveConfigurationSha256);
        Assert.Equal(
            "validated",
            dataset.EffectiveConfigurationValidation?.Status);
        var analysis = Assert.IsType<EvaluationRunAnalysis>(dataset.RunAnalysis);
        Assert.Equal("ready", analysis.Status);
        Assert.Empty(analysis.FailureCodes);
        Assert.Equal(1d, analysis.TerminalCoverage.Rate);
        Assert.Equal(1d, analysis.CostStateCoverage.Rate);
        Assert.Equal(1d, analysis.ProjectionCoverage.Rate);
        Assert.Equal(1, analysis.DecisionMatrix.Single(item =>
            item.Actual == "report" && item.Predicted == "report").Count);
        Assert.Null(analysis.DeadlineCalibration);
        Assert.Equal(0, EvaluationCommand.ExitCode(dataset));
        Assert.Equal("acme-delivery", audit.OrganizationId);
        Assert.Contains(
            "\"positionId\":\"delivery-lead\"",
            handler.RequestBody,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"positionId\":\"bug-triage\"",
            handler.RequestBody,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Manifest_analysis_is_deterministic_and_fails_incomplete_or_drifted_runs()
    {
        using var fixture = CurrentManifestFixture.Create();
        var manifest = fixture.Manifest;
        var rubric = EvaluationRubric.Load(manifest.RubricPath);
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [
                new EvaluationCase(
                    "case-001",
                    "test",
                    "A bounded synthetic context used to verify manifest analysis.",
                    CompleteReference()),
            ]);
        var prediction = CompletePrediction();
        var completeResult = Result(
            [
                GatewayCall(
                    1,
                    "directive-inference",
                    1,
                    "openai",
                    "gpt-5-mini-2025-08-07",
                    60_000,
                    8_192),
            ],
            "enforcement") with
        {
            CostStatus = "estimated",
            Prediction = prediction,
            Scoring = rubric.Score(CompleteReference(), prediction),
        };
        var completeDataset = Dataset([completeResult]);
        var complete = EvaluationRunAnalyzer.Analyze(
            corpus,
            completeDataset,
            manifest);
        var repeated = EvaluationRunAnalyzer.Analyze(
            corpus,
            completeDataset,
            manifest);

        Assert.Equal("ready", complete.Status);
        Assert.Equal(
            JsonSerializer.Serialize(complete),
            JsonSerializer.Serialize(repeated));

        var incomplete = EvaluationRunAnalyzer.Analyze(
            corpus,
            Dataset([]),
            manifest);

        Assert.Equal("not-ready", incomplete.Status);
        Assert.Equal(
            [
                "cost-state-incomplete",
                "inference-configuration-unobserved",
                "outcome-mode-unobserved",
                "projection-incomplete",
                "terminal-incomplete",
            ],
            incomplete.FailureCodes);
        Assert.Equal(
            1,
            EvaluationCommand.ExitCode(
                completeDataset with { RunAnalysis = incomplete }));

        var driftedResult = completeResult with
        {
            GatewayCalls =
            [
                GatewayCall(
                    1,
                    "directive-inference",
                    1,
                    "other-provider",
                    "other-model",
                    60_000,
                    8_192),
            ],
        };
        var drifted = EvaluationRunAnalyzer.Analyze(
            corpus,
            Dataset([driftedResult]),
            manifest);

        Assert.Equal("not-ready", drifted.Status);
        Assert.Equal(["model-drift", "provider-drift"], drifted.FailureCodes);
        Assert.Equal(
            1,
            EvaluationCommand.ExitCode(
                completeDataset with { RunAnalysis = drifted }));
    }

    [Fact]
    public async Task Prepare_command_writes_only_disposable_effective_artifacts()
    {
        using var manifestFixture = CurrentManifestFixture.Create();
        var outputDirectory = Path.Combine(
            RepositoryRoot,
            "artifacts",
            "evaluation-tests",
            Guid.NewGuid().ToString("N"));
        try
        {
            using var output = new StringWriter();
            var exitCode = await EvaluationExperimentCommand.RunAsync(
                [
                    "prepare",
                    "--manifest", manifestFixture.Manifest.ManifestPath,
                    "--output-directory", outputDirectory,
                ],
                output,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var environmentPath = Path.Combine(outputDirectory, "compose.env");
            var configurationPath = Path.Combine(
                outputDirectory,
                "effective-configuration.v2.json");
            Assert.True(File.Exists(environmentPath));
            Assert.True(File.Exists(configurationPath));
            using var document = JsonDocument.Parse(
                File.ReadAllText(configurationPath));
            Assert.Equal(
                EvaluationExperimentPreparedConfiguration.ContractName,
                document.RootElement.GetProperty("contract_name").GetString());
            Assert.DoesNotContain(
                "OPENAI_API_KEY",
                File.ReadAllText(environmentPath),
                StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(outputDirectory))
            {
                Directory.Delete(outputDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Generic_compose_adapter_is_parameterized_and_historical_overlays_remain_separate()
    {
        var compose = File.ReadAllText(
            Path.Combine(RepositoryRoot, "docker-compose.experiment.yml"));

        Assert.Contains(
            "HIVE_EXPERIMENT_ORGANIZATION_SOURCE",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_MODEL_ID",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_VERIFIER_TIMEOUT",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_EXECUTION_TIMEOUT",
            compose,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE_EXPERIMENT_PER_CALL_TIMEOUT",
            compose,
            StringComparison.Ordinal);
        Assert.Contains("read_only: true", compose, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "hybrid-outcome-resolution-reliability-v1",
            compose,
            StringComparison.Ordinal);
    }

    private static EvaluationCaseResult Result(
        IReadOnlyList<EvaluationGatewayCall> calls,
        string outcomeMode) =>
        new(
            "case-001",
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "accepted",
            202,
            "succeeded",
            "result-emitted",
            "report",
            "openai",
            "gpt-5-mini-2025-08-07",
            "json-schema",
            1,
            1,
            2,
            false,
            0.01m,
            "USD",
            true,
            100,
            110,
            GatewayCalls: calls,
            OutcomeResolutionSteps:
            [
                Resolution(outcomeMode),
            ]);

    private static EvaluationDataset Dataset(
        IReadOnlyList<EvaluationCaseResult> cases) =>
        new(
            1,
            1,
            "manifest-analysis",
            "http://localhost:8080",
            120,
            1000,
            cases);

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> CompleteReference() =>
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["decision"] = ["report"],
            ["missing-information"] = [],
            ["severity"] = ["low"],
        };

    private static EvaluationPrediction CompletePrediction() =>
        new(
            1,
            1,
            [
                new("decision", EvaluationDimensionStatuses.Valid, ["report"]),
                new("missing-information", EvaluationDimensionStatuses.Valid, []),
                new("severity", EvaluationDimensionStatuses.Valid, ["low"]),
            ]);

    private static EvaluationOutcomeResolution Resolution(string outcomeMode) =>
        new(
            outcomeMode,
            1,
            "Report.Done",
            "Completed",
            "None",
            "Report.Done",
            [],
            "policy-v1",
            new string('a', 64),
            false,
            false,
            [],
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            null);

    private static EvaluationGatewayCall GatewayCall(
        int callIndex,
        string operation,
        int iteration,
        string providerId,
        string modelId,
        double timeoutMilliseconds,
        int maxOutputTokens) =>
        new(
            callIndex,
            operation,
            iteration,
            "succeeded",
            null,
            providerId,
            modelId,
            "json-schema",
            1,
            1,
            2,
            false,
            0.01m,
            "USD",
            true,
            100,
            "estimated",
            "pricing-v1",
            1_000_000,
            0.25m,
            2m,
            RequestTimeoutMilliseconds: timeoutMilliseconds,
            MaxOutputTokens: maxOutputTokens,
            ExecutionLimitsVersion: 1,
            ExecutionBudgetMilliseconds: 90_000,
            PerCallTimeoutMilliseconds: 60_000);

    private static void WithManifestCopy(string contents, Action<string> assertion)
    {
        var directory = Path.Combine(
            RepositoryRoot,
            "artifacts",
            "evaluation-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "experiment.v1.json");
            File.WriteAllText(path, contents);
            assertion(path);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private sealed class AcceptedHandler : HttpMessageHandler
    {
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = await request.Content!
                .ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = new StringContent("{}"),
            };
        }
    }

    private sealed class SingleJourneyReader(EvaluationJourney journey) :
        IEvaluationAuditReader
    {
        public string? OrganizationId { get; private set; }

        public Task<EvaluationJourney?> ReadAsync(
            string organizationId,
            Guid threadId,
            Guid directiveId,
            CancellationToken cancellationToken)
        {
            OrganizationId = organizationId;
            return Task.FromResult<EvaluationJourney?>(journey);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class SingleProjectionReader(EvaluationPrediction prediction) :
        IEvaluationProjectionReader
    {
        public Task<EvaluationPrediction?> ReadAsync(
            string organizationId,
            Guid threadId,
            Guid directiveId,
            CancellationToken cancellationToken) =>
            Task.FromResult<EvaluationPrediction?>(prediction);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class CurrentManifestFixture : IDisposable
    {
        private readonly string _directory;

        private CurrentManifestFixture(
            string directory,
            EvaluationExperimentManifest manifest)
        {
            _directory = directory;
            Manifest = manifest;
        }

        public EvaluationExperimentManifest Manifest { get; }

        public static CurrentManifestFixture Create(string? sourceManifestPath = null)
        {
            var directory = Path.Combine(
                RepositoryRoot,
                "artifacts",
                "evaluation-tests",
                Guid.NewGuid().ToString("N"));
            var path = Path.Combine(directory, "experiment.v1.json");
            CurrentExperimentManifest.Write(
                sourceManifestPath ?? ManifestPath,
                path,
                RepositoryRoot);
            return new CurrentManifestFixture(
                directory,
                EvaluationExperimentManifest.Load(path));
        }

        public void Dispose()
        {
            if (Directory.Exists(_directory))
            {
                Directory.Delete(_directory, recursive: true);
            }
        }
    }

    private static string ManifestPath => Path.Combine(
        RepositoryRoot,
        "config",
        "experiments",
        "bug-triage-lab-v2",
        "experiment.v1.json");

    private static string HistoricalManifestPath => Path.Combine(
        RepositoryRoot,
        "config",
        "experiments",
        "bug-triage-lab-v1",
        "experiment.v1.json");

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
