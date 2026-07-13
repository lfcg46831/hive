using Hive.DemoClient.Evaluation;

namespace Hive.DemoClient.Tests;

public sealed class EvaluationAuditReaderTests
{
    private static readonly DateTimeOffset StartedAt = new(2026, 7, 13, 8, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(
        "Failed",
        "failed",
        "provider-rejected",
        "ai-gateway-failure",
        "{\"terminalCode\":\"ai-gateway-failure\"}",
        "provider-rejected")]
    [InlineData(
        "Rejected",
        "rejected",
        "policy-rejected",
        "policy-rejected",
        "{}",
        "policy-rejected")]
    public void Projects_terminal_failed_or_rejected_decision_with_gateway_cost(
        string persistedOutcome,
        string expectedOutcome,
        string gatewayReasonCode,
        string decisionReasonCode,
        string payload,
        string expectedTerminalCode)
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(-120_000, "DirectiveCreated", "Accepted"),
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                persistedOutcome,
                reasonCode: gatewayReasonCode,
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 125,
                inputTokens: 20,
                outputTokens: 4,
                totalTokens: 24,
                tokensEstimated: false,
                costAmount: 0.0123m,
                costCurrency: "USD",
                costEstimated: true,
                payload: "{\"outputConstraintMode\":\"json-schema\",\"costStatus\":\"estimated\",\"pricingVersion\":\"pricing-v1\",\"pricingTokenUnit\":\"1000000\",\"inputPricePerTokenUnit\":\"0.25\",\"outputPricePerTokenUnit\":\"2\"}"),
            Row(
                2,
                "AgentDecided",
                persistedOutcome,
                reasonCode: decisionReasonCode,
                payload: payload),
        ]);

        Assert.NotNull(journey);
        Assert.Equal(expectedOutcome, journey.Outcome);
        Assert.Equal(expectedTerminalCode, journey.TerminalCode);
        Assert.Null(journey.Decision);
        Assert.Equal("openai", journey.ProviderId);
        Assert.Equal("gpt-test", journey.ModelId);
        Assert.Equal("json-schema", journey.OutputConstraintMode);
        Assert.Equal(24, journey.TotalTokens);
        Assert.Equal(0.0123m, journey.CostAmount);
        Assert.Equal("estimated", journey.CostStatus);
        Assert.Equal("pricing-v1", journey.PricingVersion);
        Assert.Equal(1_000_000, journey.PricingTokenUnit);
        Assert.Equal(0.25m, journey.InputPricePerTokenUnit);
        Assert.Equal(2m, journey.OutputPricePerTokenUnit);
        Assert.Equal(2000, journey.JourneyDurationMilliseconds);
    }

    [Fact]
    public void Projects_result_message_with_gateway_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"completed\"}"),
            Row(2, "ResultMessageCreated", "Succeeded", messageType: "Report"),
            Row(3, "GatewayCostRecorded", "Succeeded", providerId: "stub", modelId: "triage"),
        ]);

        Assert.NotNull(journey);
        Assert.Equal("succeeded", journey.Outcome);
        Assert.Equal("completed", journey.TerminalCode);
        Assert.Equal("report", journey.Decision);
        Assert.Equal("cost-unavailable", journey.CostStatus);
        Assert.Equal(3000, journey.JourneyDurationMilliseconds);
    }

    [Fact]
    public void Does_not_project_successful_decision_without_result_message()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(1, "GatewayCostRecorded", "Succeeded"),
            Row(2, "AgentDecided", "Succeeded", payload: "{\"terminalCode\":\"completed\"}"),
        ]);

        Assert.Null(journey);
    }

    [Fact]
    public void Does_not_project_terminal_failure_without_gateway_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(
                1,
                "AgentDecided",
                "Failed",
                reasonCode: "provider-unavailable",
                payload: "{}"),
        ]);

        Assert.Null(journey);
    }

    [Fact]
    public void Projects_provider_timeout_as_terminal_with_unavailable_cost()
    {
        var journey = EvaluationJourneyProjector.TryProject(
        [
            Row(0, "SubmissionReceived", "Accepted"),
            Row(
                1,
                "GatewayCostRecorded",
                "Failed",
                reasonCode: "timeout",
                providerId: "openai",
                modelId: "gpt-test",
                latencyMilliseconds: 15_000,
                payload: "{\"costStatus\":\"cost-unavailable\",\"isRetryable\":\"True\"}"),
            Row(
                2,
                "AgentDecided",
                "Failed",
                reasonCode: "ai-gateway-failure",
                payload: "{\"terminalCode\":\"ai-gateway-failure\"}"),
        ]);

        Assert.NotNull(journey);
        Assert.Equal("failed", journey.Outcome);
        Assert.Equal("timeout", journey.TerminalCode);
        Assert.Equal("cost-unavailable", journey.CostStatus);
        Assert.Null(journey.InputTokens);
        Assert.Null(journey.OutputTokens);
        Assert.Null(journey.TotalTokens);
        Assert.Null(journey.CostAmount);
    }

    private static EvaluationAuditRow Row(
        int seconds,
        string stage,
        string outcome,
        string? reasonCode = null,
        string? messageType = null,
        string? providerId = null,
        string? modelId = null,
        int? latencyMilliseconds = null,
        int? inputTokens = null,
        int? outputTokens = null,
        int? totalTokens = null,
        bool? tokensEstimated = null,
        decimal? costAmount = null,
        string? costCurrency = null,
        bool? costEstimated = null,
        string payload = "{}") =>
        new(
            StartedAt.AddSeconds(seconds),
            stage,
            outcome,
            reasonCode,
            messageType,
            providerId,
            modelId,
            latencyMilliseconds,
            inputTokens,
            outputTokens,
            totalTokens,
            tokensEstimated,
            costAmount,
            costCurrency,
            costEstimated,
            payload);
}
