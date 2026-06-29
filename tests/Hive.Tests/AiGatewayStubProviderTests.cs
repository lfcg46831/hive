using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class AiGatewayStubProviderTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Stub_provider_returns_configured_success_with_usage_and_cost()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayStub(options =>
        {
            options.Text = "The deterministic stub handled the request.";
            options.ProviderId = "stub";
            options.ModelId = "deterministic";
            options.Usage = new StubAiGatewayUsageOptions
            {
                InputTokens = 12,
                OutputTokens = 8,
                TotalTokens = 20,
                IsEstimated = true,
            };
            options.Cost = new StubAiGatewayCostOptions
            {
                Amount = 0.03m,
                Currency = "EUR",
                IsEstimated = true,
            };
        });

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(Organization, response.OrganizationId);
        Assert.Equal(Position, response.PositionId);
        Assert.Equal(Thread, response.ThreadId);
        Assert.Equal(Message, response.MessageId);
        Assert.Equal("The deterministic stub handled the request.", response.Text);
        Assert.Equal(AiFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Provider);
        Assert.Equal("stub", response.Provider.ProviderId);
        Assert.Equal("deterministic", response.Provider.ModelId);
        Assert.NotNull(response.Usage);
        Assert.Equal(12, response.Usage.InputTokens);
        Assert.Equal(8, response.Usage.OutputTokens);
        Assert.Equal(20, response.Usage.TotalTokens);
        Assert.True(response.Usage.IsEstimated);
        Assert.NotNull(response.Cost);
        Assert.Equal(0.03m, response.Cost.Amount);
        Assert.Equal("EUR", response.Cost.Currency);
        Assert.True(response.Cost.IsEstimated);
    }

    [Fact]
    public async Task Stub_provider_returns_configured_structured_error()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayStub(options =>
        {
            options.Outcome = "error";
            options.Error = new StubAiGatewayErrorOptions
            {
                Code = "provider-rejected",
                Message = "Rejected by deterministic stub.",
                IsRetryable = false,
            };
        });

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ProviderRejected, error.Code);
        Assert.Equal("Rejected by deterministic stub.", error.Message);
        Assert.False(error.IsRetryable);
        Assert.NotNull(error.Provider);
        Assert.Equal("stub", error.Provider.ProviderId);
        Assert.Equal("deterministic", error.Provider.ModelId);
    }

    [Fact]
    public async Task Stub_provider_can_return_timeout_without_waiting()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayStub(options => options.Outcome = "timeout");

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.Timeout, error.Code);
        Assert.True(error.IsRetryable);
        Assert.NotNull(error.Provider);
    }

    [Fact]
    public async Task Stub_provider_can_return_configured_tool_call()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayStub(options =>
        {
            options.Outcome = "tool-call";
            options.Text = null;
            options.ToolCall = new StubAiGatewayToolCallOptions
            {
                Id = "call-1",
                Name = "ticket.lookup",
                Arguments =
                {
                    ["ticket"] = "HIVE-123",
                },
            };
        });

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(AiFinishReason.ToolCalls, response.FinishReason);
        Assert.Null(response.Text);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("ticket.lookup", toolCall.Name);
        Assert.Equal("HIVE-123", toolCall.Arguments["ticket"]);
    }

    [Fact]
    public async Task Stub_provider_propagates_precanceled_token()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayStub();
        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gateway.CompleteAsync(Request(), cancellation.Token));
    }

    [Fact]
    public async Task AddHiveAiGateway_activates_stub_when_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "stub",
                ["Hive:AiGateway:Stub:Text"] = "Configured from host settings.",
                ["Hive:AiGateway:Stub:Usage:InputTokens"] = "3",
                ["Hive:AiGateway:Stub:Usage:OutputTokens"] = "5",
                ["Hive:AiGateway:Stub:Usage:TotalTokens"] = "8",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal("Configured from host settings.", response.Text);
        Assert.NotNull(response.Usage);
        Assert.Equal(8, response.Usage.TotalTokens);
    }

    [Fact]
    public async Task Configured_tool_call_treats_empty_text_as_absent()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "stub",
                ["Hive:AiGateway:Stub:Outcome"] = "tool-call",
                ["Hive:AiGateway:Stub:Text"] = "",
                ["Hive:AiGateway:Stub:ToolCall:Id"] = "call-2",
                ["Hive:AiGateway:Stub:ToolCall:Name"] = "ticket.lookup",
                ["Hive:AiGateway:Stub:ToolCall:Arguments:ticket"] = "HIVE-456",
            })
            .Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Null(response.Text);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call-2", toolCall.Id);
        Assert.Equal("HIVE-456", toolCall.Arguments["ticket"]);
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");
}
