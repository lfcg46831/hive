using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class AiGatewayContractTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("engineering");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly AiProviderMetadata Provider = new("openai", "gpt-4.1");

    [Fact]
    public void Request_requires_correlation_identity()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AiGatewayRequest(null!, Position, Thread, Message, "Summarize the backlog."));
        Assert.Throws<ArgumentNullException>(
            () => new AiGatewayRequest(Organization, null!, Thread, Message, "Summarize the backlog."));
        Assert.Throws<ArgumentNullException>(
            () => new AiGatewayRequest(Organization, Position, null!, Message, "Summarize the backlog."));
        Assert.Throws<ArgumentNullException>(
            () => new AiGatewayRequest(Organization, Position, Thread, null!, "Summarize the backlog."));
    }

    [Fact]
    public void Request_snapshots_provider_neutral_context_tools_and_metadata()
    {
        var context = new List<AiGatewayMessage>
        {
            new(AiGatewayMessageRole.User, "Previous question"),
        };
        var tools = new List<AiToolDefinition>
        {
            new(
                "order.lookup",
                "Looks up an order.",
                new Dictionary<string, object?> { ["type"] = "object" }),
        };
        var metadata = new Dictionary<string, string>
        {
            ["purpose"] = "daily-report",
        };

        var request = new AiGatewayRequest(
            Organization,
            Position,
            Thread,
            Message,
            "Summarize the backlog.",
            "Answer as the engineering position.",
            context,
            tools,
            AiModelParameters.Default,
            metadata);

        context.Clear();
        tools.Clear();
        metadata.Clear();

        Assert.Equal(Organization, request.OrganizationId);
        Assert.Equal(Position, request.PositionId);
        Assert.Equal(Thread, request.ThreadId);
        Assert.Equal(Message, request.MessageId);
        Assert.Equal("Summarize the backlog.", request.Content);
        Assert.Equal("Answer as the engineering position.", request.SystemInstruction);
        Assert.Single(request.ContextMessages);
        Assert.Single(request.Tools);
        Assert.Equal("daily-report", request.Metadata["purpose"]);
    }

    [Fact]
    public void Provider_metadata_requires_provider_and_model_when_declared()
    {
        Assert.Throws<ArgumentNullException>(() => new AiProviderMetadata(null!, "gpt-4.1"));
        Assert.Throws<ArgumentException>(() => new AiProviderMetadata("", "gpt-4.1"));
        Assert.Throws<ArgumentException>(() => new AiProviderMetadata(" openai", "gpt-4.1"));
        Assert.Throws<ArgumentNullException>(() => new AiProviderMetadata("openai", null!));
        Assert.Throws<ArgumentException>(() => new AiProviderMetadata("openai", ""));
        Assert.Throws<ArgumentException>(() => new AiProviderMetadata("openai", "gpt-4.1 "));
    }

    [Fact]
    public void Success_response_requires_identity_and_cannot_carry_error()
    {
        var toolCall = new AiToolCall(
            "call-1",
            "order.lookup",
            new Dictionary<string, object?> { ["orderId"] = "A-100" });
        var usage = new AiTokenUsage(inputTokens: 10, outputTokens: 20, totalTokens: 30, isEstimated: false);
        var cost = new AiCostMetadata(amount: 0.15m, currency: "USD", isEstimated: true);

        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The backlog is on track.",
            AiFinishReason.ToolCalls,
            Provider,
            [toolCall],
            usage,
            cost);

        Assert.True(response.IsSuccess);
        Assert.False(response.IsFailure);
        Assert.Null(response.Error);
        Assert.Equal("The backlog is on track.", response.Text);
        Assert.Equal(AiFinishReason.ToolCalls, response.FinishReason);
        Assert.Equal(Provider, response.Provider);
        Assert.Equal(usage, response.Usage);
        Assert.Equal(cost, response.Cost);
        Assert.Single(response.ToolCalls);

        Assert.Throws<ArgumentNullException>(
            () => AiGatewayResponse.Succeeded(null!, Position, Thread, Message, "Text", AiFinishReason.Stop));
    }

    [Fact]
    public void Success_response_can_be_tool_call_only()
    {
        var toolCall = new AiToolCall(
            "call-1",
            "order.lookup",
            new Dictionary<string, object?> { ["orderId"] = "A-100" });

        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            text: null,
            AiFinishReason.ToolCalls,
            Provider,
            [toolCall]);

        Assert.True(response.IsSuccess);
        Assert.Null(response.Text);
        Assert.Single(response.ToolCalls);
    }

    [Fact]
    public void Failure_response_requires_structured_error_and_cannot_carry_success_payload()
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

        Assert.False(response.IsSuccess);
        Assert.True(response.IsFailure);
        Assert.Same(error, response.Error);
        Assert.Null(response.Text);
        Assert.Empty(response.ToolCalls);
        Assert.Null(response.FinishReason);
        Assert.Null(response.Usage);
        Assert.Null(response.Cost);

        Assert.Throws<ArgumentNullException>(() => AiGatewayResponse.Failed(null!));
        Assert.Empty(typeof(AiGatewayResponse).GetConstructors());
    }

    [Fact]
    public void Error_requires_correlation_identity_and_structured_code()
    {
        Assert.Throws<ArgumentNullException>(
            () => new AiGatewayError(
                null!,
                Position,
                Thread,
                Message,
                AiGatewayErrorCode.Timeout,
                "Timed out.",
                isRetryable: true));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new AiGatewayError(
                Organization,
                Position,
                Thread,
                Message,
                (AiGatewayErrorCode)0,
                "Timed out.",
                isRetryable: true));
        Assert.Throws<ArgumentException>(
            () => new AiGatewayError(
                Organization,
                Position,
                Thread,
                Message,
                AiGatewayErrorCode.Timeout,
                "",
                isRetryable: true));
    }

    [Fact]
    public void Usage_and_cost_absence_remains_explicit()
    {
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "No usage returned.",
            AiFinishReason.Stop);

        Assert.Null(response.Provider);
        Assert.Null(response.Usage);
        Assert.Null(response.Cost);
    }
}
