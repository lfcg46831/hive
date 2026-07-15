using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using System.Net;
using System.Text.Json;

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
        // Without a matching configured price, cost remains unavailable.
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
            ModelId = "gpt-5-mini-2025-08-07",
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

        var response = await Gateway(chatClient, ConfigurePricing).CompleteAsync(Request());

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
        Assert.Null(response.AppliedPricing);
    }

    [Fact]
    public async Task Estimates_cost_from_complete_usage_and_matching_model_alias()
    {
        var chatResponse = new ChatResponse(
            new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
        {
            ModelId = "gpt-5-mini-2025-08-07",
            FinishReason = ChatFinishReason.Stop,
            Usage = new UsageDetails
            {
                InputTokenCount = 1_000,
                OutputTokenCount = 500,
                TotalTokenCount = 1_500,
            },
        };
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(chatResponse));

        var response = await Gateway(chatClient, ConfigurePricing).CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(0.00125m, response.Cost?.Amount);
        Assert.Equal("USD", response.Cost?.Currency);
        Assert.True(response.Cost?.IsEstimated);
        Assert.Equal("openai-2026-07-13", response.AppliedPricing?.Version);
        Assert.Equal(1_000_000, response.AppliedPricing?.TokenUnit);
        Assert.Equal(0.25m, response.AppliedPricing?.InputPrice);
        Assert.Equal(2m, response.AppliedPricing?.OutputPrice);
    }

    [Fact]
    public async Task Missing_usage_with_matching_price_keeps_cost_unavailable()
    {
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
            {
                ModelId = "gpt-5-mini",
            }));

        var response = await Gateway(chatClient, ConfigurePricing).CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Null(response.Usage);
        Assert.Null(response.Cost);
        Assert.Null(response.AppliedPricing);
    }

    [Fact]
    public async Task Real_zero_usage_is_preserved_and_can_produce_zero_cost()
    {
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
            {
                ModelId = "gpt-5-mini",
                Usage = new UsageDetails
                {
                    InputTokenCount = 0,
                    OutputTokenCount = 0,
                    TotalTokenCount = 0,
                },
            }));

        var response = await Gateway(chatClient, ConfigurePricing).CompleteAsync(Request());

        Assert.NotNull(response.Usage);
        Assert.Equal(0, response.Usage.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
        Assert.Equal(0, response.Usage.TotalTokens);
        Assert.Equal(0m, response.Cost?.Amount);
        Assert.NotNull(response.AppliedPricing);
    }

    [Fact]
    public async Task Out_of_range_provider_usage_is_not_clamped_or_costed()
    {
        var chatClient = new FakeChatClient((_, _, _) => Task.FromResult(
            new ChatResponse(new ChatMessage(ChatRole.Assistant, "Triaged the bug."))
            {
                ModelId = "gpt-5-mini",
                Usage = new UsageDetails
                {
                    InputTokenCount = (long)int.MaxValue + 1,
                    OutputTokenCount = 1,
                    TotalTokenCount = (long)int.MaxValue + 2,
                },
            }));

        var response = await Gateway(chatClient, ConfigurePricing).CompleteAsync(Request());

        Assert.NotNull(response.Usage);
        Assert.Null(response.Usage.InputTokens);
        Assert.Equal(1, response.Usage.OutputTokens);
        Assert.Null(response.Usage.TotalTokens);
        Assert.Null(response.Cost);
        Assert.Null(response.AppliedPricing);
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

    [Theory]
    [InlineData(AiOutputConstraintMode.JsonSchema)]
    [InlineData(AiOutputConstraintMode.JsonObject)]
    [InlineData(AiOutputConstraintMode.Text)]
    public async Task Negotiates_supported_output_mode_and_maps_response_format(
        AiOutputConstraintMode supportedMode)
    {
        ChatOptions? capturedOptions = null;
        var chatClient = new FakeChatClient((_, options, _) =>
        {
            capturedOptions = options;
            return Task.FromResult(SuccessChatResponse());
        });
        var gateway = Gateway(chatClient, options =>
            options.OutputCapabilities =
            [
                AiOutputConstraintModeContract.ToWireValue(supportedMode),
            ]);

        var response = await gateway.CompleteAsync(Request(
            outputConstraint: OutputConstraint(
                AiOutputConstraintMode.JsonObject,
                AiOutputConstraintMode.Text)));

        Assert.True(response.IsSuccess);
        Assert.Equal(supportedMode, response.OutputConstraintMode);
        Assert.NotNull(capturedOptions);
        switch (supportedMode)
        {
            case AiOutputConstraintMode.JsonSchema:
                var jsonSchema = Assert.IsType<ChatResponseFormatJson>(
                    capturedOptions!.ResponseFormat);
                Assert.Equal("decision_v1", jsonSchema.SchemaName);
                Assert.Equal(
                    "object",
                    jsonSchema.Schema!.Value.GetProperty("type").GetString());
                Assert.True(Assert.IsType<bool>(
                    capturedOptions.AdditionalProperties![RealAiGatewayRequestNormalizer.StrictOptionKey]));
                break;
            case AiOutputConstraintMode.JsonObject:
                Assert.Same(ChatResponseFormat.Json, capturedOptions!.ResponseFormat);
                Assert.Null(capturedOptions.AdditionalProperties);
                break;
            case AiOutputConstraintMode.Text:
                Assert.Same(ChatResponseFormat.Text, capturedOptions!.ResponseFormat);
                Assert.Null(capturedOptions.AdditionalProperties);
                break;
        }
    }

    [Fact]
    public async Task Negotiation_prefers_json_object_over_text_when_schema_is_unavailable()
    {
        ChatOptions? capturedOptions = null;
        var chatClient = new FakeChatClient((_, options, _) =>
        {
            capturedOptions = options;
            return Task.FromResult(SuccessChatResponse());
        });
        var gateway = Gateway(chatClient, options => options.OutputCapabilities =
        [
            "text",
            "json-object",
        ]);

        var response = await gateway.CompleteAsync(Request(
            outputConstraint: OutputConstraint(
                AiOutputConstraintMode.JsonObject,
                AiOutputConstraintMode.Text)));

        Assert.True(response.IsSuccess);
        Assert.Equal(AiOutputConstraintMode.JsonObject, response.OutputConstraintMode);
        Assert.Same(ChatResponseFormat.Json, capturedOptions!.ResponseFormat);
    }

    [Fact]
    public async Task Incompatible_capabilities_fail_before_client_call_when_downgrade_is_forbidden()
    {
        var calls = 0;
        var chatClient = new FakeChatClient((_, _, _) =>
        {
            calls++;
            return Task.FromResult(SuccessChatResponse());
        });
        var gateway = Gateway(chatClient, options =>
            options.OutputCapabilities = ["text"]);

        var response = await gateway.CompleteAsync(Request(
            outputConstraint: OutputConstraint()));

        Assert.True(response.IsFailure);
        Assert.Equal(0, calls);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.OutputConstraintUnsupported, error.Code);
        Assert.False(error.IsRetryable);
        Assert.Null(response.OutputConstraintMode);
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
    public async Task Internal_timeout_is_coercive_when_provider_ignores_cancellation()
    {
        var clock = new TriggerableTimeProvider();
        var audit = new CapturingAiGatewayAuditPublisher();
        var providerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCompletion = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerToken = CancellationToken.None;
        var chatClient = new FakeChatClient((_, _, cancellationToken) =>
        {
            providerToken = cancellationToken;
            providerStarted.SetResult();
            return providerCompletion.Task;
        });

        var gateway = Gateway(chatClient, timeProvider: clock, auditPublisher: audit);
        var completion = gateway.CompleteAsync(Request(
            timeout: TimeSpan.FromSeconds(30)));

        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.False(completion.IsCompleted);
        clock.ExpireDeadline();
        var response = await completion.WaitAsync(TimeSpan.FromSeconds(1));

        Assert.True(response.IsFailure);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.Timeout, error.Code);
        Assert.True(error.IsRetryable);
        Assert.NotNull(error.Provider);
        Assert.True(providerToken.IsCancellationRequested);
        var auditEvent = Assert.Single(audit.Events);
        Assert.Equal(AiGatewayCallResult.Failed, auditEvent.Result);
        Assert.Equal(AiGatewayErrorCode.Timeout, auditEvent.ErrorCode);
        Assert.True(auditEvent.IsRetryable);

        providerCompletion.SetException(new InvalidOperationException("late failure"));
        Assert.Single(audit.Events);
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
    public async Task Caller_cancellation_interrupts_non_cooperative_provider_without_timeout_result()
    {
        var audit = new CapturingAiGatewayAuditPublisher();
        var providerStarted = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var providerCompletion = new TaskCompletionSource<ChatResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var chatClient = new FakeChatClient((_, _, _) =>
        {
            providerStarted.SetResult();
            return providerCompletion.Task;
        });
        var gateway = Gateway(chatClient, auditPublisher: audit);
        using var cancellation = new CancellationTokenSource();

        var completion = gateway.CompleteAsync(
            Request(timeout: TimeSpan.FromMinutes(1)),
            cancellation.Token);
        await providerStarted.Task.WaitAsync(TimeSpan.FromSeconds(1));
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await completion.WaitAsync(TimeSpan.FromSeconds(1)));
        Assert.Empty(audit.Events);

        providerCompletion.SetResult(SuccessChatResponse());
        Assert.Empty(audit.Events);
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
        Action<RealAiGatewayProviderOptions>? configure = null,
        TimeProvider? timeProvider = null,
        IAiGatewayAuditPublisher? auditPublisher = null)
    {
        var services = new ServiceCollection();
        services.AddSingleton(chatClient);
        if (timeProvider is not null)
        {
            services.AddSingleton(timeProvider);
        }

        if (auditPublisher is not null)
        {
            services.AddSingleton(auditPublisher);
        }

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

    private static void ConfigurePricing(RealAiGatewayProviderOptions options)
    {
        options.ModelId = "gpt-5-mini";
        options.Pricing = new AiPricingCatalogOptions
        {
            Version = "openai-2026-07-13",
            TokenUnit = 1_000_000,
            Models =
            [
                new AiModelPricingOptions
                {
                    ProviderId = "openai",
                    ModelId = "gpt-5-mini",
                    Aliases = ["gpt-5-mini-2025-08-07"],
                    InputPrice = 0.25m,
                    OutputPrice = 2m,
                    Currency = "USD",
                },
            ],
        };
    }

    private static AiGatewayRequest Request(
        string? systemInstruction = null,
        IEnumerable<AiGatewayMessage>? contextMessages = null,
        IEnumerable<AiToolDefinition>? tools = null,
        AiModelParameters? modelParameters = null,
        AiOutputConstraint? outputConstraint = null,
        TimeSpan? timeout = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            systemInstruction,
            contextMessages,
            tools,
            modelParameters,
            timeout: timeout,
            outputConstraint: outputConstraint);

    private static AiOutputConstraint OutputConstraint(
        params AiOutputConstraintMode[] allowedFallbackModes)
    {
        using var document = JsonDocument.Parse(
            """{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}""");
        return new AiOutputConstraint(
            "decision_v1",
            1,
            document.RootElement,
            allowedFallbackModes);
    }

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

    private sealed class CapturingAiGatewayAuditPublisher : IAiGatewayAuditPublisher
    {
        private readonly List<AiGatewayCostAuditEvent> _events = new();

        public IReadOnlyList<AiGatewayCostAuditEvent> Events => _events;

        public void Publish(AiGatewayCostAuditEvent @event) => _events.Add(@event);
    }

    private sealed class TriggerableTimeProvider : TimeProvider
    {
        private TriggerableTimer? _timer;

        public override ITimer CreateTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period)
        {
            var timer = new TriggerableTimer(callback, state, dueTime, period);
            if (Interlocked.CompareExchange(ref _timer, timer, null) is not null)
            {
                throw new InvalidOperationException("Only one deadline timer was expected.");
            }

            return timer;
        }

        public void ExpireDeadline()
        {
            var timer = Volatile.Read(ref _timer)
                ?? throw new InvalidOperationException("No deadline timer was scheduled.");
            timer.Fire();
        }

        private sealed class TriggerableTimer(
            TimerCallback callback,
            object? state,
            TimeSpan dueTime,
            TimeSpan period) : ITimer
        {
            private readonly object _sync = new();
            private TimeSpan _dueTime = dueTime;
            private TimeSpan _period = period;
            private bool _disposed;

            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                lock (_sync)
                {
                    if (_disposed)
                    {
                        return false;
                    }

                    _dueTime = dueTime;
                    _period = period;
                    return true;
                }
            }

            public void Dispose()
            {
                lock (_sync)
                {
                    _disposed = true;
                }
            }

            public ValueTask DisposeAsync()
            {
                Dispose();
                return ValueTask.CompletedTask;
            }

            public void Fire()
            {
                lock (_sync)
                {
                    if (_disposed || _dueTime == Timeout.InfiniteTimeSpan)
                    {
                        return;
                    }

                    _dueTime = _period;
                }

                callback(state);
            }
        }
    }
}
