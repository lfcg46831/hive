using System.Text.Json;
using System.Net;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationExperimentManifestTests
{
    [Fact]
    public void Tracked_manifest_resolves_hashes_and_effective_configuration()
    {
        var manifest = EvaluationExperimentManifest.Load(ManifestPath);

        Assert.Equal(EvaluationExperimentManifest.ContractName, manifest.Name);
        Assert.Equal(1, manifest.ManifestVersion);
        Assert.Equal("bug-triage-lab-v1", manifest.ExperimentId);
        Assert.Equal("prepared", manifest.Status);
        Assert.Equal("acme-delivery", manifest.Organization.OrganizationId);
        Assert.Equal("delivery-lead", manifest.Organization.SourcePositionId);
        Assert.Equal("bug-triage", manifest.Organization.PositionId);
        Assert.Equal("openai", manifest.Model.ProviderId);
        Assert.Equal("gpt-5-mini-2025-08-07", manifest.Model.ModelId);
        Assert.Equal(60_000, manifest.Limits.ProviderTimeoutMilliseconds);
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
        Assert.DoesNotContain("APIKEY", environment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("PASSWORD", environment, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("SECRET", environment, StringComparison.OrdinalIgnoreCase);

        var prepared = manifest.PreparedConfiguration();
        Assert.Equal(
            EvaluationExperimentPreparedConfiguration.ContractName,
            prepared.Name);
        Assert.Equal(manifest.ManifestSha256, prepared.ManifestSha256);
        Assert.Equal(
            manifest.EffectiveConfigurationSha256,
            prepared.EffectiveConfigurationSha256);
        Assert.Equal(
            "config/experiments/hybrid-outcome-resolution-reliability-v1/organization.yaml",
            prepared.Configuration.OrganizationConfigurationPath);
    }

    [Fact]
    public void Manifest_rejects_unknown_properties_and_hash_drift()
    {
        var original = File.ReadAllText(ManifestPath);
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
                "2d531b8fde0ee141d7c0e48562917a2c9ae9d2e29c2f01422204f0b62f4ac421",
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
    }

    [Fact]
    public void Manifest_drives_run_scope_and_disallows_runtime_overrides()
    {
        var options = EvaluationRunOptions.Parse(
            [
                "--run-id", "manifest-test",
                "--manifest", ManifestPath,
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
                    "--manifest", ManifestPath,
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
        var manifest = EvaluationExperimentManifest.Load(ManifestPath);
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

        var drifted = Result(
            [
                GatewayCall(
                    1,
                    "directive-inference",
                    1,
                    "other",
                    "other-model",
                    61_000,
                    4_096),
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
                "max-output-tokens-drift",
                "model-drift",
                "outcome-mode-drift",
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
        var manifest = EvaluationExperimentManifest.Load(ManifestPath);
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
                    new Dictionary<string, IReadOnlyList<string>>()),
            ]);

        var dataset = await new EvaluationRunner(client, audit)
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
    public async Task Prepare_command_writes_only_disposable_effective_artifacts()
    {
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
                    "--manifest", ManifestPath,
                    "--output-directory", outputDirectory,
                ],
                output,
                CancellationToken.None);

            Assert.Equal(0, exitCode);
            var environmentPath = Path.Combine(outputDirectory, "compose.env");
            var configurationPath = Path.Combine(
                outputDirectory,
                "effective-configuration.v1.json");
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
            MaxOutputTokens: maxOutputTokens);

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

    private static string ManifestPath => Path.Combine(
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
