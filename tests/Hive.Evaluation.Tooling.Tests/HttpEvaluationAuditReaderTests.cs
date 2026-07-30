using System.Net;
using System.Net.Http.Json;
using Hive.Contracts.Audit;
using Hive.Evaluation.Tooling.Evaluation;

namespace Hive.Evaluation.Tooling.Tests;

public sealed class HttpEvaluationAuditReaderTests
{
    private static readonly Guid Thread =
        Guid.Parse("aaaaaaaa-0000-0000-0000-000000000116");
    private static readonly Guid Directive =
        Guid.Parse("bbbbbbbb-0000-0000-0000-000000000116");
    private static readonly Guid Message =
        Guid.Parse("cccccccc-0000-0000-0000-000000000116");
    private static readonly DateTimeOffset At =
        new(2026, 7, 30, 11, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Reader_consumes_only_the_public_http_contract_and_caches_terminal_data()
    {
        var handler = new ContractHandler(Page());
        using var client = new HttpClient(handler);
        var rubric = EvaluationRubric.Load(Path.Combine(
            RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "evaluation",
            "bug-triage-rubric.v1.json"));
        await using var reader = new HttpEvaluationAuditReader(
            client,
            new Uri("http://runtime.local"),
            rubric);

        var journey = await reader.ReadAsync(
            "acme-delivery",
            Thread,
            Directive,
            CancellationToken.None);
        var prediction = await ((IEvaluationProjectionReader)reader).ReadAsync(
            "acme-delivery",
            Thread,
            Directive,
            CancellationToken.None);

        Assert.NotNull(journey);
        Assert.Equal("succeeded", journey.Outcome);
        Assert.Equal("report", journey.Decision);
        Assert.Equal(15, journey.TotalTokens);
        Assert.Equal(125, journey.GatewayLatencyMilliseconds);
        Assert.NotNull(prediction);
        Assert.Equal(
            ["medium"],
            Assert.Single(prediction.Dimensions, item => item.DimensionId == "severity").Labels);
        Assert.Equal(
            ["environment"],
            Assert.Single(
                prediction.Dimensions,
                item => item.DimensionId == "missing-information").Labels);
        Assert.Equal(
            ["report"],
            Assert.Single(prediction.Dimensions, item => item.DimensionId == "decision").Labels);
        Assert.Equal(1, handler.CallCount);
        Assert.Equal(
            $"/api/v1/organizations/acme-delivery/threads/{Thread:D}/directives/{Directive:D}/audit-export?after_sequence=0",
            handler.LastRequestUri!.PathAndQuery);
    }

    private static DirectiveAuditExportPage Page()
    {
        var resultContent =
            """{"Body":"Done.\nhive-evaluation-v1:{\"dimensions\":{\"severity\":[\"medium\"],\"missing-information\":[\"environment\"]}}"}""";
        return new DirectiveAuditExportPage(
            AuditExportContract.Name,
            AuditExportContract.Version,
            "acme-delivery",
            Thread,
            Directive,
            0,
            3,
            isTerminal: true,
            [
                Event(1, "SubmissionReceived", "Accepted", At),
                new AuditExportEvent(
                    2,
                    Guid.Parse("dddddddd-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(100),
                    At.AddMilliseconds(101),
                    "GatewayCostRecorded",
                    "Succeeded",
                    Message,
                    provider: new AuditExportProvider("stub", "triage"),
                    usage: new AuditExportUsage(10, 5, 15, estimated: false),
                    cost: new AuditExportCost(0.01m, "USD", estimated: false),
                    latencyMilliseconds: 125,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["costStatus"] = "measured",
                        ["outputConstraintMode"] = "json-schema",
                    }),
                new AuditExportEvent(
                    3,
                    Guid.Parse("eeeeeeee-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(250),
                    At.AddMilliseconds(251),
                    "ResultMessageCreated",
                    "Succeeded",
                    Message,
                    messageType: "Report"),
            ],
            AuditExportResult.Create("Report", 1, resultContent));
    }

    private static AuditExportEvent Event(
        long sequence,
        string stage,
        string outcome,
        DateTimeOffset occurredAt) =>
        new(
            sequence,
            Guid.Parse("ffffffff-0000-0000-0000-000000000116"),
            occurredAt,
            occurredAt.AddMilliseconds(1),
            stage,
            outcome,
            Message);

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

    private sealed class ContractHandler(DirectiveAuditExportPage page) : HttpMessageHandler
    {
        public int CallCount { get; private set; }

        public Uri? LastRequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequestUri = request.RequestUri;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(page),
            });
        }
    }
}
