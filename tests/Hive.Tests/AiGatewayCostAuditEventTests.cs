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
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop,
            Provider,
            usage: usage,
            cost: cost);

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
        Assert.Null(audit.ErrorCode);
        Assert.Null(audit.IsRetryable);
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
            provider: Provider);
}
