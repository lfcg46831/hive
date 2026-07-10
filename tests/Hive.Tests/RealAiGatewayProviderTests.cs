using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;

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
        // Cost computation is US-F0-07-T10, not this adapter.
        Assert.Null(response.Cost);
        Assert.Empty(response.ToolCalls);
    }

    [Fact]
    public async Task Maps_response_metadata_usage_and_declared_cost_to_hive_contract()
    {
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            ResponseId = "response-123",
            ModelId = "gpt-4o-mini-2024",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = 19,
                OutputTokenCount = 8,
                TotalTokenCount = 27,
            },
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["hive.cost.amount"] = "0.0142",
                ["hive.cost.currency"] = "EUR",
                ["hive.cost.isEstimated"] = true,
            },
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.NotNull(response.Provider);
        Assert.Equal("response-123", response.Provider.Metadata["response-id"]);
        Assert.NotNull(response.Usage);
        Assert.Equal(19, response.Usage.InputTokens);
        Assert.Equal(8, response.Usage.OutputTokens);
        Assert.Equal(27, response.Usage.TotalTokens);
        Assert.NotNull(response.Cost);
        Assert.Equal(0.0142m, response.Cost.Amount);
        Assert.Equal("EUR", response.Cost.Currency);
        Assert.True(response.Cost.IsEstimated);
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
    public async Task Whitespace_text_with_tool_call_maps_to_tool_call_success_without_text()
    {
        var message = new ChatMessage(ChatRole.Assistant, new List<AIContent>
        {
            new TextContent("   "),
            new FunctionCallContent(
                "call-1",
                "ticket.lookup",
                new Dictionary<string, object?> { ["ticket"] = "HIVE-123" }),
        });
        var chatResponse = new ChatResponse(message);
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Null(response.Text);
        Assert.Equal(AiFinishReason.ToolCalls, response.FinishReason);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call-1", toolCall.Id);
        Assert.Equal("ticket.lookup", toolCall.Name);
    }

    [Fact]
    public async Task Normalizes_system_context_and_current_content_in_canonical_order()
    {
        List<ChatMessage>? capturedMessages = null;
        var chatClient = new FakeChatClient((messages, _, _) =>
        {
            capturedMessages = messages.ToList();
            return Task.FromResult(SuccessChatResponse());
        });
        var request = Request(
            systemInstruction: "Answer as the triage position.",
            contextMessages:
            [
                new AiGatewayMessage(AiGatewayMessageRole.User, "Earlier request."),
                new AiGatewayMessage(AiGatewayMessageRole.Assistant, "Earlier answer."),
                new AiGatewayMessage(AiGatewayMessageRole.Tool, "ticket=HIVE-123"),
            ]);

        await Gateway(chatClient).CompleteAsync(request);

        Assert.NotNull(capturedMessages);
        Assert.Equal(
            [
                ChatRole.System,
                ChatRole.User,
                ChatRole.Assistant,
                ChatRole.Tool,
                ChatRole.User,
            ],
            capturedMessages!.Select(message => message.Role));
        Assert.Equal(
            [
                "Answer as the triage position.",
                "Earlier request.",
                "Earlier answer.",
                "ticket=HIVE-123",
                "Classify this bug.",
            ],
            capturedMessages.Select(message => message.Text));
    }

    [Fact]
    public async Task Normalizes_model_parameters_with_request_values_overriding_provider_defaults()
    {
        ChatOptions? capturedOptions = null;
        var chatClient = new FakeChatClient((_, options, _) =>
        {
            capturedOptions = options;
            return Task.FromResult(SuccessChatResponse());
        });
        var request = Request(modelParameters: new AiModelParameters(
            temperature: 0.8m,
            maxOutputTokens: 42));

        await Gateway(chatClient, options =>
        {
            options.Temperature = 0.2m;
            options.MaxOutputTokens = 256;
        }).CompleteAsync(request);

        Assert.NotNull(capturedOptions);
        Assert.Equal("gpt-4o-mini", capturedOptions!.ModelId);
        Assert.Equal(0.8f, capturedOptions.Temperature);
        Assert.Equal(42, capturedOptions.MaxOutputTokens);
    }

    [Fact]
    public async Task Normalizes_absent_model_parameters_from_provider_defaults()
    {
        ChatOptions? capturedOptions = null;
        var chatClient = new FakeChatClient((_, options, _) =>
        {
            capturedOptions = options;
            return Task.FromResult(SuccessChatResponse());
        });

        await Gateway(chatClient, options =>
        {
            options.Temperature = 0.4m;
            options.MaxOutputTokens = 128;
        }).CompleteAsync(Request());

        Assert.NotNull(capturedOptions);
        Assert.Equal(0.4f, capturedOptions!.Temperature);
        Assert.Equal(128, capturedOptions.MaxOutputTokens);
    }

    [Fact]
    public async Task Normalizes_tool_definitions_to_non_invocable_function_declarations()
    {
        ChatOptions? capturedOptions = null;
        var chatClient = new FakeChatClient((_, options, _) =>
        {
            capturedOptions = options;
            return Task.FromResult(SuccessChatResponse());
        });
        var request = Request(tools:
        [
            AiToolActingUnderSchema.Compose(
                new AiToolDefinition(
                "ticket.lookup",
                "Looks up a ticket.",
                new Dictionary<string, object?>
                {
                    ["type"] = "object",
                    ["properties"] = new Dictionary<string, object?>
                    {
                        ["ticket"] = new Dictionary<string, object?>
                        {
                            ["type"] = "string",
                        },
                    },
                    ["required"] = new[] { "ticket" },
                }),
                [
                    AuthorityKey.From("zeta.scope"),
                    AuthorityKey.From("alpha.scope"),
                    AuthorityKey.From("zeta.scope"),
                ]),
        ]);

        await Gateway(chatClient).CompleteAsync(request);

        Assert.NotNull(capturedOptions);
        var tool = Assert.Single(capturedOptions!.Tools!);
        var declaration = Assert.IsAssignableFrom<AIFunctionDeclaration>(tool);
        Assert.Equal("ticket.lookup", declaration.Name);
        Assert.Equal("Looks up a ticket.", declaration.Description);
        Assert.Equal(
            "string",
            declaration
                .JsonSchema
                .GetProperty("properties")
                .GetProperty("ticket")
                .GetProperty("type")
                .GetString());
        var actingUnder = declaration
            .JsonSchema
            .GetProperty("properties")
            .GetProperty("acting_under");
        Assert.Equal("string", actingUnder.GetProperty("type").GetString());
        Assert.Equal(
            ["alpha.scope", "zeta.scope"],
            actingUnder
                .GetProperty("enum")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
        Assert.Equal(
            ["ticket", "acting_under"],
            declaration
                .JsonSchema
                .GetProperty("required")
                .EnumerateArray()
                .Select(value => value.GetString())
                .ToArray());
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
    public async Task Malformed_declared_cost_maps_to_invalid_provider_response()
    {
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            FinishReason = ChatFinishReason.Stop,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["hive.cost.amount"] = "not-a-decimal",
                ["hive.cost.currency"] = "EUR",
                ["hive.cost.isEstimated"] = true,
            },
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.InvalidProviderResponse, error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task Non_finite_declared_cost_maps_to_invalid_provider_response()
    {
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            FinishReason = ChatFinishReason.Stop,
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["hive.cost.amount"] = double.NaN,
                ["hive.cost.currency"] = "EUR",
                ["hive.cost.isEstimated"] = true,
            },
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.InvalidProviderResponse, error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public void Response_normalizer_captures_redactable_raw_snapshot()
    {
        var normalizer = new RealAiGatewayResponseNormalizer();
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            ResponseId = "response-123",
            ModelId = "gpt-4o-mini-2024",
            RawRepresentation = "{\"id\":\"response-123\",\"secret\":\"redact-me\"}",
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["provider-region"] = "eu-west",
                ["complex-provider-value"] = new Dictionary<string, object?>
                {
                    ["secret"] = "redact-me",
                },
            },
        };

        var normalized = normalizer.Normalize(
            Request(),
            chatResponse,
            Settings());

        Assert.True(normalized.Response.IsSuccess);
        Assert.Null(normalized.Response.Cost);
        Assert.NotNull(normalized.RawResponse);
        Assert.Equal("openai", normalized.RawResponse.ProviderId);
        Assert.Equal("gpt-4o-mini-2024", normalized.RawResponse.ModelId);
        Assert.Equal("response-123", normalized.RawResponse.ResponseId);
        Assert.Equal(
            "{\"id\":\"response-123\",\"secret\":\"redact-me\"}",
            normalized.RawResponse.RawRepresentation);
        Assert.Equal("eu-west", normalized.RawResponse.AdditionalProperties["provider-region"]);
        Assert.DoesNotContain(
            "redact-me",
            normalized.RawResponse.AdditionalProperties["complex-provider-value"]);
        Assert.Contains("TextContent", normalized.RawResponse.ContentTypes);
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
            throw new InvalidOperationException(
                $"upstream returned 503 with payload {ApiKey}"));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.True(error.IsRetryable);
        Assert.Contains("provider-unavailable", error.Message);
        Assert.DoesNotContain("upstream returned 503", error.Message);
        Assert.DoesNotContain(ApiKey, error.Message);
        Assert.DoesNotContain(nameof(InvalidOperationException), error.Message);
        Assert.NotNull(error.Provider);
        Assert.Equal("openai", error.Provider.ProviderId);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, AiGatewayErrorCode.CredentialsMissing, false)]
    [InlineData(HttpStatusCode.Forbidden, AiGatewayErrorCode.CredentialsMissing, false)]
    [InlineData(HttpStatusCode.RequestTimeout, AiGatewayErrorCode.Timeout, true)]
    [InlineData(HttpStatusCode.TooManyRequests, AiGatewayErrorCode.QuotaExceeded, true)]
    [InlineData(HttpStatusCode.BadRequest, AiGatewayErrorCode.ProviderRejected, false)]
    [InlineData(HttpStatusCode.NotFound, AiGatewayErrorCode.ProviderRejected, false)]
    [InlineData(HttpStatusCode.Conflict, AiGatewayErrorCode.ProviderRejected, false)]
    [InlineData(HttpStatusCode.UnprocessableEntity, AiGatewayErrorCode.ProviderRejected, false)]
    [InlineData(HttpStatusCode.InternalServerError, AiGatewayErrorCode.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, AiGatewayErrorCode.ProviderUnavailable, true)]
    [InlineData(HttpStatusCode.GatewayTimeout, AiGatewayErrorCode.Timeout, true)]
    public async Task Provider_http_status_failures_map_to_structured_errors(
        HttpStatusCode statusCode,
        AiGatewayErrorCode expectedCode,
        bool expectedRetryable)
    {
        var chatClient = new FakeChatClient((_, _, _) =>
            throw new HttpRequestException(
                $"provider payload {ApiKey}",
                inner: null,
                statusCode));

        var response = await Gateway(chatClient).CompleteAsync(Request());

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(expectedCode, error.Code);
        Assert.Equal(expectedRetryable, error.IsRetryable);
        Assert.Contains(((int)statusCode).ToString(), error.Message);
        Assert.DoesNotContain("provider payload", error.Message);
        Assert.DoesNotContain(ApiKey, error.Message);
        Assert.DoesNotContain(nameof(HttpRequestException), error.Message);
        Assert.NotNull(error.Provider);
        Assert.Equal("openai", error.Provider.ProviderId);
    }

    private static ChatResponse SuccessChatResponse() =>
        new(new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            FinishReason = ChatFinishReason.Stop,
        };

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

    private static RealAiGatewayProviderSettings Settings() =>
        new(
            ApiKey,
            new AiProviderMetadata("openai", "gpt-4o-mini"),
            new AiModelParameters());

    private static AiGatewayRequest Request(
        string? systemInstruction = null,
        IEnumerable<AiGatewayMessage>? contextMessages = null,
        IEnumerable<AiToolDefinition>? tools = null,
        AiModelParameters? modelParameters = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            systemInstruction,
            contextMessages,
            tools,
            modelParameters);

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
