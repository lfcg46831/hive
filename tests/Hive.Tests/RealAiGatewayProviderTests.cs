using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class RealAiGatewayProviderTests
{
    private const string ApiKey = "secret-key";

    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void AddHiveAiGateway_activates_real_openai_provider_when_configured()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "real",
                ["Hive:AiGateway:Real:ProviderId"] = "openai",
                ["Hive:AiGateway:Real:ModelId"] = "gpt-4o-mini",
                ["Hive:AiGateway:Real:ApiKey"] = ApiKey,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetRequiredService<IChatClient>());
        Assert.Equal(
            "RealAiGatewayProvider",
            provider.GetRequiredService<IAiGatewayProvider>().GetType().Name);
        Assert.IsType<AiGateway>(provider.GetRequiredService<IAiGateway>());
    }

    [Fact]
    public void AddHiveAiGateway_rejects_unsupported_real_provider_without_leaking_secret()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "real",
                ["Hive:AiGateway:Real:ProviderId"] = "azure-openai",
                ["Hive:AiGateway:Real:ModelId"] = "gpt-4o-mini",
                ["Hive:AiGateway:Real:ApiKey"] = ApiKey,
            })
            .Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);

        using var provider = services.BuildServiceProvider();

        var exception = Assert.Throws<InvalidOperationException>(
            () => provider.GetRequiredService<IAiGatewayProvider>());
        Assert.Contains("configuration-invalid", exception.Message);
        Assert.Contains("unsupported", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ApiKey, exception.Message);
    }

    [Fact]
    public async Task Maps_successful_response_to_hive_contract()
    {
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            ModelId = "gpt-4o-mini-2024",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = 11,
                OutputTokenCount = 7,
                TotalTokenCount = 18,
            },
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(Organization, response.OrganizationId);
        Assert.Equal(Position, response.PositionId);
        Assert.Equal(Thread, response.ThreadId);
        Assert.Equal(Message, response.MessageId);
        Assert.Equal("Triaged the bug.", response.Text);
        Assert.Equal(AiFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Provider);
        Assert.Equal("openai", response.Provider.ProviderId);
        // The provider/model reported uses the response model id when present.
        Assert.Equal("gpt-4o-mini-2024", response.Provider.ModelId);
        Assert.NotNull(response.Usage);
        Assert.Equal(11, response.Usage.InputTokens);
        Assert.Equal(7, response.Usage.OutputTokens);
        Assert.Equal(18, response.Usage.TotalTokens);
        // Cost computation is US-F0-07-T09/T10, not this adapter.
        Assert.Null(response.Cost);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Maps_function_call_to_tool_call()
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new FunctionCallContent(
                "call-1",
                "ticket.lookup",
                new Dictionary<string, object?> { ["ticket"] = "HIVE-123" }),
        });
        var chatResponse = new ChatResponse(message)
        {
            FinishReason = ChatFinishReason.ToolCalls,
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(AiFinishReason.ToolCalls, response.FinishReason);
        Assert.Null(response.Text);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("ticket.lookup", toolCall.Name);
        Assert.Equal("HIVE-123", toolCall.Arguments["ticket"]);
        Assert.NotNull(response.Provider);
        // Response omits the model id: falls back to the configured default.
        Assert.Equal("gpt-4o-mini", response.Provider.ModelId);
    }

    [Fact]
    public async Task Empty_response_maps_to_invalid_provider_response()
    {
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, string.Empty))
        {
            FinishReason = ChatFinishReason.Stop,
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.InvalidProviderResponse, error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task Internal_timeout_maps_to_retryable_timeout()
    {
        var chatClient = new FakeChatClient(async (_, _, cancellationToken) =>
        {
            await Task.Delay(Timeout.Infinite, cancellationToken);
            return new ChatResponse(new ChatMessage(ChatRole.Assistant, "unreachable"));
        });

        var gateway = Gateway(chatClient, options => options.TimeoutSeconds = 1);

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.Timeout, error.Code);
        Assert.True(error.IsRetryable);
        Assert.NotNull(error.Provider);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated()
    {
        var chatClient = new FakeChatClient((_, _, _) =>
            Task.FromResult(new ChatResponse(
                new ChatMessage(ChatRole.Assistant, "unreachable"))));
        var gateway = Gateway(chatClient);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gateway.CompleteAsync(Request(), cancellation.Token));
    }

    [Fact]
    public async Task Provider_exception_maps_to_provider_unavailable_without_revealing_secret()
    {
        var chatClient = new FakeChatClient((_, _, _) =>
            throw new InvalidOperationException("upstream returned 503"));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.True(error.IsRetryable);
        Assert.Contains("upstream returned 503", error.Message);
        Assert.DoesNotContain(ApiKey, error.Message);
        Assert.NotNull(error.Provider);
        Assert.Equal("openai", error.Provider.ProviderId);
    }

    private static IAiGateway Gateway(
        IChatClient chatClient,
        Action<RealAiGatewayProviderOptions>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClient);
        services.AddHiveAiGatewayReal(options =>
        {
            options.ProviderId = "openai";
            options.ModelId = "gpt-4o-mini";
            options.ApiKey = ApiKey;
            configure?.Invoke(options);
        });

        return services
            .BuildServiceProvider()
            .GetRequiredService<IAiGateway>();
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");

    private sealed class FakeChatClient : IChatClient
    {
        private readonly Func<
            IEnumerable<ChatMessage>,
            ChatOptions?,
            CancellationToken,
            Task<ChatResponse>> _handler;

        public FakeChatClient(
            Func<IEnumerable<ChatMessage>, ChatOptions?, CancellationToken, Task<ChatResponse>> handler)
        {
            _handler = handler;
        }

        public Task<ChatResponse> GetResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            _handler(messages, options, cancellationToken);

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
            IEnumerable<ChatMessage> messages,
            ChatOptions? options = null,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
