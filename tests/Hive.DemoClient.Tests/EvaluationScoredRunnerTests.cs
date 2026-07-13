using System.Net;
using Hive.DemoClient.Evaluation;

namespace Hive.DemoClient.Tests;

public sealed class EvaluationScoredRunnerTests
{
    [Fact]
    public async Task Runner_joins_predictions_scores_cases_and_computes_macro_mean()
    {
        using var client = new HttpClient(new AcceptedHandler());
        var rubric = EvaluationRubric.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "evaluation",
            "bug-triage-rubric.v1.json"));
        var runner = new EvaluationRunner(
            client,
            new SuccessfulAuditReader(),
            projectionReader: new OrderedProjectionReader(
                new EvaluationPrediction(1, "medium", []),
                new EvaluationPrediction(1, "critical", ["run-log"])),
            rubric: rubric);
        var corpus = new EvaluationCorpus(
            1,
            "evaluation-example",
            [
                Case("triage-001"),
                Case("triage-002"),
            ]);

        var dataset = await runner.RunAsync(corpus, Options(), CancellationToken.None);

        Assert.Equal(1, dataset.ProjectionVersion);
        Assert.Equal(1, dataset.RubricVersion);
        Assert.Equal("scored", dataset.Cases[0].Scoring?.Status);
        Assert.Equal(1d, dataset.Cases[0].Scoring?.CaseScore);
        Assert.Equal("scored", dataset.Cases[1].Scoring?.Status);
        Assert.Equal(0.30d, dataset.Cases[1].Scoring?.CaseScore);
        Assert.Equal(0.65d, dataset.CorpusScore);
        Assert.Equal("critical", dataset.Cases[1].Prediction?.Severity);
        Assert.Equal(["run-log"], dataset.Cases[1].Prediction?.MissingInformation);
    }

    [Fact]
    public async Task Runner_marks_absent_projection_as_structured_scoring_failure()
    {
        using var client = new HttpClient(new AcceptedHandler());
        var rubric = EvaluationRubric.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "evaluation",
            "bug-triage-rubric.v1.json"));
        var runner = new EvaluationRunner(
            client,
            new SuccessfulAuditReader(),
            projectionReader: NoopEvaluationProjectionReader.Instance,
            rubric: rubric);

        var result = Assert.Single((await runner.RunAsync(
            new EvaluationCorpus(1, "evaluation-example", [Case("triage-001")]),
            Options(),
            CancellationToken.None)).Cases);

        Assert.Null(result.Prediction);
        Assert.Equal("failed", result.Scoring?.Status);
        Assert.Contains("severity-prediction-missing", result.Scoring!.FailureCodes);
        Assert.Contains("missing-information-prediction-missing", result.Scoring.FailureCodes);
        Assert.Equal(0d, result.Scoring.Dimensions.Severity);
        Assert.Equal(0d, result.Scoring.Dimensions.MissingInformation);
        Assert.Equal(1d, result.Scoring.Dimensions.Decision);
    }

    private static EvaluationCase Case(string id) =>
        new(
            id,
            "test",
            "Evaluation context.",
            new EvaluationHumanReference("medium", [], "test", "report"));

    private static EvaluationRunOptions Options() =>
        new(
            RepositoryRoot,
            "scored-run",
            new Uri("http://localhost:8080"),
            "Host=localhost",
            "corpus.json",
            "output.json",
            TimeSpan.FromSeconds(1),
            TimeSpan.FromMilliseconds(1),
            EvaluationRunOptions.DefaultSentAt);

    private sealed class AcceptedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.Accepted));
    }

    private sealed class SuccessfulAuditReader : IEvaluationAuditReader
    {
        public Task<EvaluationJourney?> ReadAsync(
            string organizationId,
            Guid threadId,
            Guid directiveId,
            CancellationToken cancellationToken) =>
            Task.FromResult<EvaluationJourney?>(new EvaluationJourney(
                "succeeded", "completed", "report", "stub", "triage", "json-schema",
                10, 4, 14, false, 0.01m, "USD", true, 50, 75));

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class OrderedProjectionReader(params EvaluationPrediction[] predictions)
        : IEvaluationProjectionReader
    {
        private int _index;

        public Task<EvaluationPrediction?> ReadAsync(
            string organizationId,
            Guid threadId,
            Guid directiveId,
            CancellationToken cancellationToken) =>
            Task.FromResult<EvaluationPrediction?>(
                predictions[Math.Min(_index++, predictions.Length - 1)]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string RepositoryRoot
    {
        get
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
}
