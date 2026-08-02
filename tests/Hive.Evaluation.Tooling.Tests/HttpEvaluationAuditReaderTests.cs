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
        Assert.Equal(0.01m, journey.CostAmount);
        Assert.True(journey.CostEstimated);
        Assert.Equal("estimated", journey.CostStatus);
        Assert.Equal("pricing-v2", journey.PricingVersion);
        Assert.Equal(1_000_000, journey.PricingTokenUnit);
        Assert.Equal(0.25m, journey.InputPricePerTokenUnit);
        Assert.Equal(2m, journey.OutputPricePerTokenUnit);
        Assert.Equal(125, journey.GatewayLatencyMilliseconds);
        var gatewayCall = Assert.Single(journey.GatewayCalls!);
        Assert.Equal("pricing-v2", gatewayCall.PricingVersion);
        Assert.Equal(0.25m, gatewayCall.InputPricePerTokenUnit);
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

    [Fact]
    public async Task Reader_preserves_multi_call_amount_but_not_incomplete_pricing()
    {
        var handler = new ContractHandler(MultipleCallPage());
        using var client = new HttpClient(handler);
        await using var reader = new HttpEvaluationAuditReader(
            client,
            new Uri("http://runtime.local"),
            LoadRubric());

        var journey = await reader.ReadAsync(
            "acme-delivery",
            Thread,
            Directive,
            CancellationToken.None);

        Assert.NotNull(journey);
        Assert.Equal(0.03m, journey.CostAmount);
        Assert.Equal("USD", journey.CostCurrency);
        Assert.True(journey.CostEstimated);
        Assert.Equal("estimated", journey.CostStatus);
        Assert.Null(journey.PricingVersion);
        Assert.Null(journey.PricingTokenUnit);
        Assert.Null(journey.InputPricePerTokenUnit);
        Assert.Null(journey.OutputPricePerTokenUnit);
        var calls = Assert.IsAssignableFrom<IReadOnlyList<EvaluationGatewayCall>>(
            journey.GatewayCalls);
        Assert.Equal(2, calls.Count);
        Assert.Equal("pricing-v2", calls[0].PricingVersion);
        Assert.Equal(0.25m, calls[0].InputPricePerTokenUnit);
        Assert.Null(calls[1].PricingVersion);
        Assert.Null(calls[1].PricingTokenUnit);
        Assert.Null(calls[1].InputPricePerTokenUnit);
        Assert.Null(calls[1].OutputPricePerTokenUnit);
    }

    [Fact]
    public async Task Reader_scores_authoritative_decision_with_valid_accepted_observation_after_override()
    {
        var handler = new ContractHandler(OverridePage(proposalOverridden: true));
        using var client = new HttpClient(handler);
        var rubric = LoadRubric();
        await using var reader = new HttpEvaluationAuditReader(
            client,
            new Uri("http://runtime.local"),
            rubric);

        var prediction = await ((IEvaluationProjectionReader)reader).ReadAsync(
            "acme-delivery",
            Thread,
            Directive,
            CancellationToken.None);
        var scoring = rubric.Score(
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
            {
                ["severity"] = ["medium"],
                ["missing-information"] = ["environment"],
                ["decision"] = ["escalation"],
            },
            prediction);

        Assert.NotNull(prediction);
        Assert.Equal(
            ["medium"],
            Assert.Single(
                prediction.Dimensions,
                item => item.DimensionId == "severity").Labels);
        Assert.Equal(
            ["environment"],
            Assert.Single(
                prediction.Dimensions,
                item => item.DimensionId == "missing-information").Labels);
        Assert.Equal(
            ["escalation"],
            Assert.Single(
                prediction.Dimensions,
                item => item.DimensionId == "decision").Labels);
        Assert.Equal("scored", scoring.Status);
        Assert.Equal(1d, scoring.CaseScore);
    }

    [Fact]
    public async Task Reader_ignores_accepted_observation_without_an_authoritative_override()
    {
        var handler = new ContractHandler(OverridePage(proposalOverridden: false));
        using var client = new HttpClient(handler);
        await using var reader = new HttpEvaluationAuditReader(
            client,
            new Uri("http://runtime.local"),
            LoadRubric());

        var prediction = await ((IEvaluationProjectionReader)reader).ReadAsync(
            "acme-delivery",
            Thread,
            Directive,
            CancellationToken.None);

        Assert.NotNull(prediction);
        Assert.Equal(
            EvaluationDimensionStatuses.Missing,
            Assert.Single(
                prediction.Dimensions,
                item => item.DimensionId == "severity").Status);
        Assert.Equal(
            ["escalation"],
            Assert.Single(
                prediction.Dimensions,
                item => item.DimensionId == "decision").Labels);
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
                    cost: new AuditExportCost(
                        0.01m,
                        "USD",
                        estimated: true,
                        "pricing-v2",
                        1_000_000,
                        0.25m,
                        2m),
                    latencyMilliseconds: 125,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["costStatus"] = "estimated",
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

    private static DirectiveAuditExportPage MultipleCallPage() =>
        new(
            AuditExportContract.Name,
            AuditExportContract.Version,
            "acme-delivery",
            Thread,
            Directive,
            0,
            5,
            isTerminal: true,
            [
                Event(1, "SubmissionReceived", "Accepted", At),
                new AuditExportEvent(
                    2,
                    Guid.Parse("33333333-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(100),
                    At.AddMilliseconds(101),
                    "GatewayCostRecorded",
                    "Succeeded",
                    Message,
                    provider: new AuditExportProvider("openai", "gpt-test"),
                    usage: new AuditExportUsage(10, 5, 15, estimated: false),
                    cost: new AuditExportCost(
                        0.01m,
                        "USD",
                        estimated: false,
                        "pricing-v2",
                        1_000_000,
                        0.25m,
                        2m),
                    latencyMilliseconds: 100,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["costStatus"] = "provider-reported",
                        ["operation"] = "directive-inference",
                    }),
                new AuditExportEvent(
                    3,
                    Guid.Parse("44444444-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(200),
                    At.AddMilliseconds(201),
                    "GatewayCostRecorded",
                    "Succeeded",
                    Message,
                    provider: new AuditExportProvider("openai", "gpt-test"),
                    usage: new AuditExportUsage(8, 2, 10, estimated: false),
                    cost: new AuditExportCost(0.02m, "USD", estimated: true),
                    latencyMilliseconds: 75,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["costStatus"] = "estimated",
                        ["operation"] = "outcome-verification",
                        ["pricingVersion"] = "pricing-v2",
                        ["pricingTokenUnit"] = "1000000",
                        ["outputPricePerTokenUnit"] = "2",
                    }),
                new AuditExportEvent(
                    4,
                    Guid.Parse("55555555-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(300),
                    At.AddMilliseconds(301),
                    "AgentDecided",
                    "Succeeded",
                    Message,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["terminalCode"] = "result-emitted",
                    }),
                new AuditExportEvent(
                    5,
                    Guid.Parse("66666666-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(400),
                    At.AddMilliseconds(401),
                    "ResultMessageCreated",
                    "Succeeded",
                    Message,
                    messageType: "Report"),
            ]);

    private static DirectiveAuditExportPage OverridePage(bool proposalOverridden)
    {
        const string finalContent =
            "{\"Context\":\"The authoritative resolver requires escalation.\"}";
        const string observationContent =
            "{\"dimensions\":{\"missing-information\":[\"environment\"],\"severity\":[\"medium\"]}}";
        return new DirectiveAuditExportPage(
            AuditExportContract.Name,
            AuditExportContract.Version,
            "acme-delivery",
            Thread,
            Directive,
            0,
            2,
            isTerminal: true,
            [
                new AuditExportEvent(
                    1,
                    Guid.Parse("11111111-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(200),
                    At.AddMilliseconds(201),
                    "OutcomeResolved",
                    "Succeeded",
                    Message,
                    attributes: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["proposalOverridden"] = proposalOverridden ? "true" : "false",
                    }),
                new AuditExportEvent(
                    2,
                    Guid.Parse("22222222-0000-0000-0000-000000000116"),
                    At.AddMilliseconds(250),
                    At.AddMilliseconds(251),
                    "ResultMessageCreated",
                    "Succeeded",
                    Message,
                    messageType: "Escalation"),
            ],
            AuditExportResult.Create(
                "Escalation",
                1,
                finalContent,
                AuditExportAcceptedObservation.Create(1, observationContent)));
    }

    private static EvaluationRubric LoadRubric() => EvaluationRubric.Load(Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "evaluation",
        "bug-triage-rubric.v1.json"));

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
