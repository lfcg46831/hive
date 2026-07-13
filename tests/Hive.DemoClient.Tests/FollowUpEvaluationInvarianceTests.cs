using System.Net;
using System.Text.Json;
using Hive.DemoClient.Evaluation;

namespace Hive.DemoClient.Tests;

public sealed class FollowUpEvaluationInvarianceTests
{
    [Fact]
    public async Task Second_role_fixture_uses_the_same_reader_scorers_and_safe_serializer()
    {
        var corpus = EvaluationCorpus.Load(FixturePath("follow-up-coordination-corpus.v1.json"));
        var rubric = EvaluationRubric.Load(FixturePath("follow-up-coordination-rubric.v1.json"));
        rubric.ValidateCorpus(corpus);
        using var client = new HttpClient(new AcceptedHandler());
        var runner = new EvaluationRunner(
            client,
            new SuccessfulAuditReader(),
            projectionReader: new OrderedProjectionReader(
                Prediction(
                    Valid("coordination-route", "track"),
                    Valid("pending-signals"),
                    Valid("response-window", "next-cycle")),
                Prediction(
                    Valid("response-window", "next-cycle"),
                    Valid("pending-signals", "attendee_reply"),
                    Valid("coordination-route", "track")),
                Prediction(
                    Valid("pending-signals", "private-rejected-value"),
                    Valid("coordination-route", "request-support"),
                    Valid("response-window", "now"))),
            rubric: rubric);

        var dataset = await runner.RunAsync(
            corpus,
            Options(),
            CancellationToken.None);

        Assert.Equal(3, dataset.Cases.Count);
        Assert.Equal(1d, dataset.Cases[0].Scoring?.CaseScore);
        Assert.Equal(
            (0.40d * 0.5d) + (0.35d * (2d / 3d)) + 0.25d,
            dataset.Cases[1].Scoring?.CaseScore);
        Assert.Equal("failed", dataset.Cases[2].Scoring?.Status);
        Assert.Equal(["projection-invalid"], dataset.Cases[2].Scoring?.FailureCodes);
        Assert.Equal(0.65d, dataset.Cases[2].Scoring?.CaseScore);
        Assert.Equal(
            dataset.Cases.Average(item => item.Scoring!.CaseScore),
            dataset.CorpusScore);
        Assert.All(dataset.Cases, item => Assert.Equal(
            ["coordination-route", "pending-signals", "response-window"],
            item.Prediction?.Dimensions.Select(dimension => dimension.DimensionId)));

        var json = JsonSerializer.Serialize(dataset);

        Assert.Contains("\"dimension_id\":\"response-window\"", json, StringComparison.Ordinal);
        Assert.Contains("\"dimension_id\":\"pending-signals\"", json, StringComparison.Ordinal);
        Assert.Contains("\"labels\":[\"attendee_reply\"]", json, StringComparison.Ordinal);
        Assert.DoesNotContain("private-rejected-value", json, StringComparison.Ordinal);
        Assert.DoesNotContain(corpus.Cases[0].Context, json, StringComparison.Ordinal);
        Assert.DoesNotContain("human_reference", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("severity", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("missing-information", json, StringComparison.OrdinalIgnoreCase);
    }

    private static EvaluationPrediction Prediction(
        params EvaluationDimensionPrediction[] dimensions) => new(1, 1, dimensions);

    private static EvaluationDimensionPrediction Valid(string id, params string[] labels) =>
        new(id, EvaluationDimensionStatuses.Valid, labels);

    private static EvaluationRunOptions Options() => new(
        RepositoryRoot,
        "follow-up-invariance",
        new Uri("http://localhost:8080"),
        "Host=localhost",
        FixturePath("follow-up-coordination-corpus.v1.json"),
        Path.Combine(RepositoryRoot, "artifacts", "evaluation", "follow-up-invariance.json"),
        TimeSpan.FromSeconds(1),
        TimeSpan.FromMilliseconds(1),
        EvaluationRunOptions.DefaultSentAt,
        FixturePath("follow-up-coordination-rubric.v1.json"));

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
                "succeeded",
                "completed",
                "report",
                "stub",
                "coordination",
                "json-schema",
                10,
                4,
                14,
                false,
                0.01m,
                "USD",
                true,
                50,
                75));

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
            Task.FromResult<EvaluationPrediction?>(predictions[_index++]);

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static string FixturePath(string fileName) => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        fileName);

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

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }
}
