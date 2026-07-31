using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class AiGatewayCostAuditEventTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly AiProviderMetadata Provider = new("openai", "gpt-4.1");
    private static readonly DateTimeOffset StartedAt =
        new(2026, 6, 30, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset CompletedAt = StartedAt.AddMilliseconds(275);

    [Fact]
    public void FromResponse_projects_success_with_correlation_usage_cost_and_duration()
    {
        var usage = new AiTokenUsage(11, 7, 18, isEstimated: false);
        var cost = new AiCostMetadata(0.014m, "EUR", isEstimated: true);
        var appliedPricing = new AiAppliedPricing(
            "price-v1",
            1_000_000,
            1m,
            2m,
            "EUR");
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop,
            Provider,
            usage: usage,
            cost: cost,
            appliedPricing: appliedPricing);

        var audit = AiGatewayCostAuditEvent.FromResponse(
            Request(),
            response,
            StartedAt,
            CompletedAt);

        Assert.Equal(Organization, audit.OrganizationId);
        Assert.Equal(Position, audit.PositionId);
        Assert.Equal(Thread, audit.ThreadId);
        Assert.Equal(Message, audit.MessageId);
        Assert.Equal(StartedAt, audit.StartedAt);
        Assert.Equal(CompletedAt, audit.CompletedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(275), audit.Duration);
        Assert.Equal(AiGatewayCallResult.Succeeded, audit.Result);
        Assert.Equal(Provider, audit.Provider);
        Assert.Equal(usage, audit.Usage);
        Assert.Equal(cost, audit.Cost);
        Assert.Equal(appliedPricing, audit.AppliedPricing);
        Assert.Equal(AiCostStatus.Estimated, audit.CostStatus);
        Assert.Null(audit.ErrorCode);
        Assert.Null(audit.IsRetryable);
        Assert.Equal("outcome-verification", audit.Operation);
        Assert.Equal(2, audit.Iteration);
        Assert.Equal(1, audit.ExecutionLimitsVersion);
        Assert.Equal(TimeSpan.FromSeconds(90), audit.ExecutionBudget);
        Assert.Equal(TimeSpan.FromSeconds(60), audit.PerCallTimeout);
    }

    [Fact]
    public void FromResponse_projects_failure_with_error_code_and_retryability()
    {
        var error = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.Timeout,
            "The provider timed out.",
            isRetryable: true,
            Provider);
        var response = AiGatewayResponse.Failed(error);

        var audit = AiGatewayCostAuditEvent.FromResponse(
            Request(),
            response,
            StartedAt,
            CompletedAt);

        Assert.Equal(AiGatewayCallResult.Failed, audit.Result);
        Assert.Equal(Provider, audit.Provider);
        Assert.Equal(AiGatewayErrorCode.Timeout, audit.ErrorCode);
        Assert.True(audit.IsRetryable);
        Assert.Null(audit.Usage);
        Assert.Null(audit.Cost);
        Assert.Null(audit.AppliedPricing);
        Assert.Equal(AiCostStatus.Unavailable, audit.CostStatus);
    }

    [Fact]
    public void FromResponse_projects_minimized_diagnostics_from_invalid_provider_response()
    {
        var usage = new AiTokenUsage(1_000, 4_096, 5_096);
        var cost = new AiCostMetadata(0.008442m, "USD", isEstimated: true);
        var pricing = new AiAppliedPricing(
            "openai-2026-07-13",
            1_000_000,
            0.25m,
            2m,
            "USD");
        var provider = new AiProviderMetadata(
            "openai",
            "gpt-5-mini-2025-08-07",
            new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["response-id"] = "response-empty-123",
            });
        var response = AiGatewayResponse.Failed(new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.InvalidProviderResponse,
            "AI gateway real provider returned neither text nor a tool call.",
            isRetryable: false,
            provider,
            new AiGatewayFailureDiagnostics(
                AiFinishReason.Length,
                usage,
                cost,
                pricing)));

        var audit = AiGatewayCostAuditEvent.FromResponse(
            Request(),
            response,
            StartedAt,
            CompletedAt);

        Assert.Equal(AiGatewayCallResult.Failed, audit.Result);
        Assert.Equal(provider, audit.Provider);
        Assert.Equal(usage, audit.Usage);
        Assert.Equal(cost, audit.Cost);
        Assert.Equal(pricing, audit.AppliedPricing);
        Assert.Equal(AiCostStatus.Estimated, audit.CostStatus);
        Assert.Equal(AiGatewayErrorCode.InvalidProviderResponse, audit.ErrorCode);
        Assert.False(audit.IsRetryable);
        Assert.Equal(AiFinishReason.Length, audit.FinishReason);
        Assert.Equal(TimeSpan.FromSeconds(60), audit.RequestTimeout);
        Assert.Equal(8192, audit.MaxOutputTokens);
    }

    [Fact]
    public void Provider_declared_cost_is_reported_even_when_provider_marks_it_estimated()
    {
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop,
            Provider,
            usage: new AiTokenUsage(11, 7, 18),
            cost: new AiCostMetadata(0.014m, "EUR", isEstimated: true));

        var audit = AiGatewayCostAuditEvent.FromResponse(
            Request(),
            response,
            StartedAt,
            CompletedAt);

        Assert.Equal(AiCostStatus.ProviderReported, audit.CostStatus);
        Assert.True(audit.Cost?.IsEstimated);
        Assert.Null(audit.AppliedPricing);
    }

    [Fact]
    public void Constructor_rejects_negative_duration_and_ambiguous_result_payload()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AiGatewayCostAuditEvent(
                Organization,
                Position,
                Thread,
                Message,
                CompletedAt,
                StartedAt,
                AiGatewayCallResult.Succeeded));

        Assert.Throws<ArgumentException>(
            () => new AiGatewayCostAuditEvent(
                Organization,
                Position,
                Thread,
                Message,
                StartedAt,
                CompletedAt,
                AiGatewayCallResult.Succeeded,
                errorCode: AiGatewayErrorCode.Timeout,
                isRetryable: true));

        Assert.Throws<ArgumentException>(
            () => new AiGatewayCostAuditEvent(
                Organization,
                Position,
                Thread,
                Message,
                StartedAt,
                CompletedAt,
                AiGatewayCallResult.Failed));
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            modelParameters: new AiModelParameters(maxOutputTokens: 8192),
            metadata: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["hive.operation"] = "outcome-verification",
                ["iteration"] = "2",
                ["hive.execution-limits-version"] = "1",
                ["hive.execution-budget-ms"] = "90000",
                ["hive.per-call-timeout-ms"] = "60000",
            },
            provider: Provider,
            timeout: TimeSpan.FromSeconds(60));
}
