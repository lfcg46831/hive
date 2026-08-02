using System.Net;
using System.Text;
using System.Text.Json;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class EvaluationRunnerTests
{
    [Fact]
    public void Loads_and_orders_the_versioned_evaluation_corpus()
    {
        var corpus = EvaluationCorpus.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "evaluation",
            "bug-triage-corpus.v1.json"));

        Assert.Equal(1, corpus.CorpusVersion);
        Assert.Equal(30, corpus.Cases.Count);
        Assert.Equal("triage-001", corpus.Cases[0].CaseId);
        Assert.Equal("triage-030", corpus.Cases[^1].CaseId);
    }

    [Fact]
    public async Task Submits_cases_sequentially_and_extracts_reproducible_results()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var audit = new RecordingAuditReader(
            null,
            Journey("report", "openai", "gpt-test"),
            Journey("escalation", "openai", "gpt-test"));
        var runner = new EvaluationRunner(client, audit);

        var dataset = await runner.RunAsync(
            Corpus(("triage-002", "Second context"), ("triage-001", "First context")),
            Options("run-one"),
            CancellationToken.None);

        Assert.Equal(["triage-001", "triage-002"], dataset.Cases.Select(item => item.CaseId));
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("First context", handler.Requests[0], StringComparison.Ordinal);
        Assert.Contains("Second context", handler.Requests[1], StringComparison.Ordinal);
        Assert.Equal(3, audit.Calls);
        Assert.Equal("report", dataset.Cases[0].Decision);
        Assert.Equal("escalation", dataset.Cases[1].Decision);
        Assert.All(dataset.Cases, item => Assert.Equal("succeeded", item.Outcome));
        Assert.All(dataset.Cases, item => Assert.Equal(202, item.HttpStatus));
        Assert.NotEqual(dataset.Cases[0].ThreadId, dataset.Cases[1].ThreadId);

        var repeated = await new EvaluationRunner(
            new HttpClient(new RecordingHandler(HttpStatusCode.Accepted)),
            new RecordingAuditReader(Journey("report", "openai", "gpt-test"), Journey("report", "openai", "gpt-test")))
            .RunAsync(Corpus(("triage-001", "First context"), ("triage-002", "Second context")), Options("run-one"), CancellationToken.None);

        Assert.Equal(dataset.Cases.Select(item => item.MessageId), repeated.Cases.Select(item => item.MessageId));

        var nextRun = await new EvaluationRunner(
            new HttpClient(new RecordingHandler(HttpStatusCode.Accepted)),
            new RecordingAuditReader(Journey("report", "openai", "gpt-test"), Journey("report", "openai", "gpt-test")))
            .RunAsync(Corpus(("triage-002", "Second context"), ("triage-001", "First context")), Options("run-two"), CancellationToken.None);

        Assert.All(dataset.Cases, previous =>
            Assert.DoesNotContain(nextRun.Cases, current =>
                current.MessageId == previous.MessageId
                || current.ThreadId == previous.ThreadId
                || current.DirectiveId == previous.DirectiveId));
    }

    [Fact]
    public async Task Preserves_the_minimized_outcome_resolution_sequence_in_the_dataset()
    {
        var step = new EvaluationOutcomeResolution(
            "enforcement",
            1,
            "Report.Done",
            "Completed",
            "None",
            "Escalation",
            ["verifier-disagreement"],
            "outcome-policy-v1",
            "sha256:policy",
            true,
            true,
            [],
            "openai",
            "gpt-test",
            10,
            2,
            12,
            0.001m,
            "USD",
            5,
            VerifierStatus: "Classified",
            VerifierClassification: "Undetermined",
            SemanticCompletionCandidate: false,
            SemanticCompletionIneligibilityReasons:
            [
                "evidence-source-not-directive-input",
                "evidence-reference-not-in-context",
            ],
            DeadlineRemainingMilliseconds: 9000);
        var journey = Journey("escalation", "openai", "gpt-test") with
        {
            OutcomeResolution = step,
            OutcomeResolutionSteps = [step],
        };
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.Accepted));
        var runner = new EvaluationRunner(client, new RecordingAuditReader(journey));

        var result = Assert.Single((await runner.RunAsync(
            Corpus(("triage-001", "Context")),
            Options("run-resolution-steps"),
            CancellationToken.None)).Cases);

        Assert.Same(step, result.OutcomeResolution);
        Assert.Equal([step], result.OutcomeResolutionSteps);
    }

    [Fact]
    public async Task Records_submission_rejection_and_continues_with_remaining_cases()
    {
        var handler = new RecordingHandler(HttpStatusCode.BadRequest, HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var runner = new EvaluationRunner(client, new RecordingAuditReader(Journey("report", "openai", "gpt-test")));

        var dataset = await runner.RunAsync(
            Corpus(("triage-001", "Rejected context"), ("triage-002", "Accepted context")),
            Options("run-rejection"),
            CancellationToken.None);

        Assert.Equal("rejected", dataset.Cases[0].SubmissionStatus);
        Assert.Equal("submission-rejected", dataset.Cases[0].Outcome);
        Assert.Equal("succeeded", dataset.Cases[1].Outcome);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Records_terminal_failure_with_cost_and_continues_with_remaining_cases()
    {
        var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var client = new HttpClient(handler);
        var audit = new RecordingAuditReader(
            new EvaluationJourney(
                "failed", "provider-unavailable", null, "openai", "gpt-test", "json-schema",
                10, 0, 10, false, 0.01m, "USD", true, 50, 75,
                "estimated", "pricing-v1", 1_000_000, 0.25m, 2m),
            Journey("report", "openai", "gpt-test"));
        var runner = new EvaluationRunner(client, audit);

        var dataset = await runner.RunAsync(
            Corpus(("triage-001", "Failing context"), ("triage-002", "Successful context")),
            Options("run-terminal-failure"),
            CancellationToken.None);

        Assert.Equal("failed", dataset.Cases[0].Outcome);
        Assert.Equal("provider-unavailable", dataset.Cases[0].TerminalCode);
        Assert.Null(dataset.Cases[0].Decision);
        Assert.Equal(0.01m, dataset.Cases[0].CostAmount);
        Assert.Equal("estimated", dataset.Cases[0].CostStatus);
        Assert.Equal("pricing-v1", dataset.Cases[0].PricingVersion);
        Assert.Equal(1_000_000, dataset.Cases[0].PricingTokenUnit);
        Assert.Equal("succeeded", dataset.Cases[1].Outcome);
        Assert.Equal(2, audit.Calls);
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task Times_out_without_inventing_metrics()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.Accepted));
        var runner = new EvaluationRunner(client, new RecordingAuditReader());
        var options = Options("run-timeout") with
        {
            Timeout = TimeSpan.FromMilliseconds(5),
            PollInterval = TimeSpan.FromMilliseconds(1),
        };

        var result = Assert.Single((await runner.RunAsync(
            Corpus(("triage-001", "Timeout context")),
            options,
            CancellationToken.None)).Cases);

        Assert.Equal("timeout", result.Outcome);
        Assert.Null(result.Decision);
        Assert.Null(result.CostAmount);
        Assert.Null(result.TotalTokens);
    }

    [Fact]
    public async Task Provider_timeout_terminal_finishes_on_first_poll_with_unavailable_cost()
    {
        using var client = new HttpClient(new RecordingHandler(HttpStatusCode.Accepted));
        var audit = new RecordingAuditReader(new EvaluationJourney(
            "failed", "timeout", null, "openai", "gpt-test", "json-schema",
            null, null, null, null, null, null, null, 15_000, 15_100,
            "cost-unavailable"));
        var runner = new EvaluationRunner(client, audit);

        var result = Assert.Single((await runner.RunAsync(
            Corpus(("triage-001", "Provider timeout context")),
            Options("run-provider-timeout"),
            CancellationToken.None)).Cases);

        Assert.Equal(1, audit.Calls);
        Assert.Equal("failed", result.Outcome);
        Assert.Equal("timeout", result.TerminalCode);
        Assert.Equal("cost-unavailable", result.CostStatus);
        Assert.Null(result.CostAmount);
        Assert.Null(result.TotalTokens);
    }

    [Fact]
    public void Dataset_json_contains_only_safe_evaluation_fields()
    {
        var result = new EvaluationCaseResult(
            "triage-001", Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "accepted", 202,
            "succeeded", "completed", "report", "openai", "gpt-test", "json-schema", 10, 4, 14,
            false, 0.01m, "USD", true, 50, 75, "estimated", "pricing-v1", 1_000_000,
            0.25m, 2m) with
        {
            Prediction = new EvaluationPrediction(
                1,
                1,
                [
                    new EvaluationDimensionPrediction(
                        "opaque-dimension",
                        EvaluationDimensionStatuses.Valid,
                        ["admitted-label"]),
                ]),
            Scoring = new EvaluationCaseScoring(
                "scored",
                [],
                [
                    new EvaluationDimensionScoring(
                        "opaque-dimension",
                        EvaluationDimensionStatuses.Valid,
                        ["admitted-label"],
                        1d),
                ],
                1d),
            GatewayCalls =
            [
                GatewayCall(1, withPricing: true),
                GatewayCall(2, withPricing: true),
            ],
        };
        var withoutPricing = result with
        {
            CaseId = "triage-002",
            PricingVersion = null,
            PricingTokenUnit = null,
            InputPricePerTokenUnit = null,
            OutputPricePerTokenUnit = null,
            GatewayCalls =
            [
                GatewayCall(1, withPricing: false),
                GatewayCall(2, withPricing: false),
            ],
        };
        var dataset = new EvaluationDataset(
            1,
            1,
            "run-safe",
            "http://localhost:8080",
            120,
            1000,
            [result, withoutPricing]);

        var json = JsonSerializer.Serialize(dataset);
        var roundTripped = JsonSerializer.Deserialize<EvaluationDataset>(json);

        Assert.Contains("\"run_id\":\"run-safe\"", json);
        Assert.Contains("\"output_constraint_mode\":\"json-schema\"", json);
        Assert.Contains("\"cost_status\":\"estimated\"", json);
        Assert.Contains("\"pricing_version\":\"pricing-v1\"", json);
        Assert.Contains("\"input_price_per_token_unit\":0.25", json);
        Assert.Contains("\"output_price_per_token_unit\":2", json);
        Assert.Contains("\"dimension_id\":\"opaque-dimension\"", json);
        Assert.Contains("\"status\":\"valid\"", json);
        Assert.Contains("\"labels\":[\"admitted-label\"]", json);
        Assert.Contains("\"score\":1", json);
        Assert.DoesNotContain("context", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("human_reference", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("connection", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("response", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(roundTripped);
        Assert.Equal(0.25m, roundTripped.Cases[0].InputPricePerTokenUnit);
        Assert.Equal(2m, roundTripped.Cases[0].OutputPricePerTokenUnit);
        var completeCalls = Assert.IsAssignableFrom<IReadOnlyList<EvaluationGatewayCall>>(
            roundTripped.Cases[0].GatewayCalls);
        Assert.Equal(2, completeCalls.Count);
        Assert.All(completeCalls, call =>
        {
            Assert.Equal("pricing-v1", call.PricingVersion);
            Assert.Equal(0.25m, call.InputPricePerTokenUnit);
            Assert.Equal(2m, call.OutputPricePerTokenUnit);
        });
        Assert.Null(roundTripped.Cases[1].PricingVersion);
        Assert.Null(roundTripped.Cases[1].PricingTokenUnit);
        Assert.Null(roundTripped.Cases[1].InputPricePerTokenUnit);
        Assert.Null(roundTripped.Cases[1].OutputPricePerTokenUnit);
        var absentCalls = Assert.IsAssignableFrom<IReadOnlyList<EvaluationGatewayCall>>(
            roundTripped.Cases[1].GatewayCalls);
        Assert.Equal(2, absentCalls.Count);
        Assert.All(absentCalls, call =>
        {
            Assert.Null(call.PricingVersion);
            Assert.Null(call.PricingTokenUnit);
            Assert.Null(call.InputPricePerTokenUnit);
            Assert.Null(call.OutputPricePerTokenUnit);
        });
    }

    [Fact]
    public void Run_analysis_reports_outcome_resolution_quality_and_operational_metrics()
    {
        var corpus = Corpus(
            ("case-positive", "Escalation expected"),
            ("case-negative", "Report expected")) with
        {
            Cases =
            [
                Corpus(("case-positive", "Escalation expected")).Cases[0] with
                {
                    HumanReference = new Dictionary<string, IReadOnlyList<string>>
                    {
                        ["decision"] = ["escalation"],
                    },
                },
                Corpus(("case-negative", "Report expected")).Cases[0],
            ],
        };
        var dataset = new EvaluationDataset(
            1,
            1,
            "run-outcomes",
            "http://localhost:8080",
            120,
            1000,
            [
                OutcomeCase(
                    "case-positive",
                    new EvaluationOutcomeResolution(
                        "enforcement", 1, "ContinueWork", "InProgress", "None",
                        "ContinueWork", ["autonomous-action-available"],
                        "outcome-policy-v1", "sha256:one", false, false,
                        [], "openai", "gpt-test", 10, 2, 12, 0.001m, "USD", 4,
                        SemanticCompletionCandidate: false,
                        SemanticCompletionIneligibilityReasons:
                        [
                            "proposal-intent-not-report-done",
                            "work-state-not-completed",
                            "next-action-present",
                        ])),
                OutcomeCase(
                    "case-negative",
                    new EvaluationOutcomeResolution(
                        "enforcement", 1, "Report.Progress", "InProgress", "None",
                        "Escalation", ["routing-unavailable"],
                        "outcome-policy-v1", "sha256:one", true, true,
                        ["facts-unavailable"], "openai", "gpt-test", 10, 2, 12,
                        0.001m, "USD", 5,
                        VerifierStatus: "Classified",
                        VerifierClassification: "Undetermined",
                        SemanticCompletionCandidate: true)),
            ]);
        var plan = new EvaluationPlan
        {
            Provider = new EvaluationProviderFreeze
            {
                ProviderId = "openai",
                ModelIds = ["gpt-test"],
                OutputConstraintMode = "json-schema",
                PricingVersion = "pricing-v1",
            },
            DecisionAnalysis = new EvaluationDecisionAnalysisFreeze
            {
                DimensionId = "decision",
                NegativeLabel = "report",
                PositiveLabel = "escalation",
            },
            DeadlineCalibration = new EvaluationDeadlineFreeze(),
        };

        var analysis = EvaluationRunAnalyzer.Analyze(
            corpus,
            dataset,
            plan,
            EvaluationPlan.CalibrationPartition);

        var outcomes = Assert.IsType<EvaluationOutcomeResolutionAnalysis>(
            analysis.OutcomeResolution);
        Assert.Equal(new EvaluationCoverage(2, 2, 1d), outcomes.Coverage);
        Assert.Equal(2, outcomes.ProposalResolutionMatrix.Count);
        Assert.Equal(1, outcomes.OverrideCount);
        Assert.Equal(0.5d, outcomes.OverrideRate);
        Assert.Equal(1, outcomes.VerifierCount);
        Assert.Equal(0.5d, outcomes.VerifierRate);
        Assert.Equal(1, outcomes.UndeterminedCount);
        Assert.Equal(0.5d, outcomes.UndeterminedRate);
        Assert.Equal(1, outcomes.FalseNegativeCount);
        Assert.Equal(1, outcomes.FalsePositiveCount);
        Assert.Equal(2, outcomes.Reasons.Count);
        Assert.Contains(outcomes.Reasons, item =>
            item.Code == "routing-unavailable" && item.CaseCount == 1);
        Assert.Equal(2, outcomes.Latency.ObservedCount);
        Assert.Equal(5, outcomes.Latency.P95Milliseconds);
        var diagnostic = Assert.Single(outcomes.Diagnostics);
        Assert.Equal("facts-unavailable", diagnostic.Code);
        Assert.Equal(1, diagnostic.CaseCount);
        Assert.Equal(
            [
                "next-action-present",
                "proposal-intent-not-report-done",
                "work-state-not-completed",
            ],
            outcomes.SemanticCompletionIneligibilityReasons!
                .Select(item => item.Code));
    }

    [Fact]
    public void Options_require_canonical_run_id()
    {
        var invalid = Assert.Throws<ArgumentException>(() => EvaluationRunOptions.Parse(
            ["--run-id", "Invalid Run"],
            RepositoryRoot));
        Assert.Contains("run-id", invalid.Message);

        var valid = EvaluationRunOptions.Parse(
            ["--run-id", "model-a-001"],
            RepositoryRoot);
        Assert.Equal("model-a-001", valid.RunId);
        Assert.EndsWith("model-a-001.json", valid.OutputPath, StringComparison.Ordinal);
        Assert.EndsWith("bug-triage-rubric.v1.json", valid.RubricPath, StringComparison.Ordinal);
    }

    private static EvaluationCorpus Corpus(params (string Id, string Context)[] cases) =>
        new(1, "evaluation-example", cases.Select(item => new EvaluationCase(
            item.Id,
            "test",
            item.Context,
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["severity"] = ["medium"],
                ["missing-information"] = [],
                ["decision"] = ["report"],
            })).ToArray());

    private static EvaluationRunOptions Options(string runId) => new(
        RepositoryRoot,
        runId,
        new Uri("http://localhost:8080"),
        "corpus.json",
        "output.json",
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(1),
        EvaluationRunOptions.DefaultSentAt);

    private static EvaluationCaseResult OutcomeCase(
        string caseId,
        EvaluationOutcomeResolution resolution) =>
        new(
            caseId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            "accepted",
            202,
            "succeeded",
            "completed",
            "report",
            "openai",
            "gpt-test",
            "json-schema",
            10,
            2,
            12,
            false,
            0.001m,
            "USD",
            false,
            4,
            5,
            "provider-reported",
            "pricing-v1",
            1_000_000,
            0.25m,
            2m,
            OutcomeResolution: resolution);

    private static EvaluationJourney Journey(string decision, string provider, string model) => new(
        "succeeded", "completed", decision, provider, model, "json-schema",
        10, 4, 14, false, 0.01m, "USD", true, 50, 75,
        "estimated", "pricing-v1", 1_000_000, 0.25m, 2m);

    private static EvaluationGatewayCall GatewayCall(int callIndex, bool withPricing) =>
        new(
            callIndex,
            "directive-inference",
            callIndex,
            "succeeded",
            ReasonCode: null,
            ProviderId: "openai",
            ModelId: "gpt-test",
            OutputConstraintMode: "json-schema",
            InputTokens: 10,
            OutputTokens: 4,
            TotalTokens: 14,
            TokensEstimated: false,
            CostAmount: 0.01m,
            CostCurrency: "USD",
            CostEstimated: true,
            LatencyMilliseconds: 50,
            CostStatus: "estimated",
            PricingVersion: withPricing ? "pricing-v1" : null,
            PricingTokenUnit: withPricing ? 1_000_000 : null,
            InputPricePerTokenUnit: withPricing ? 0.25m : null,
            OutputPricePerTokenUnit: withPricing ? 2m : null);

    private sealed class RecordingHandler(params HttpStatusCode[] statuses) : HttpMessageHandler
    {
        private int _index;
        public List<string> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
            var status = statuses[Math.Min(_index++, statuses.Length - 1)];
            return new HttpResponseMessage(status)
            {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class RecordingAuditReader(params EvaluationJourney?[] journeys) : IEvaluationAuditReader
    {
        private int _index;
        public int Calls { get; private set; }

        public Task<EvaluationJourney?> ReadAsync(
            string organizationId,
            Guid threadId,
            Guid directiveId,
            CancellationToken cancellationToken)
        {
            Calls++;
            var journey = journeys.Length == 0
                ? null
                : journeys[Math.Min(_index++, journeys.Length - 1)];
            return Task.FromResult(journey);
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
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
