using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class AiGatewayIntegrationTests
{
    private const string RealTestApiKeyEnvironmentVariable =
        "HIVE_AI_GATEWAY_REAL_TEST_API_KEY";
    private const string RealTestModelIdEnvironmentVariable =
        "HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID";
    private const string RealTestEndpointEnvironmentVariable =
        "HIVE_AI_GATEWAY_REAL_TEST_ENDPOINT";

    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Configured_stub_gateway_completes_success_through_di()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Hive:AiGateway:Provider"] = "stub",
            ["Hive:AiGateway:Stub:ProviderId"] = "stub",
            ["Hive:AiGateway:Stub:ModelId"] = "integration-deterministic",
            ["Hive:AiGateway:Stub:Text"] = "Integration stub response.",
            ["Hive:AiGateway:Stub:Usage:InputTokens"] = "13",
            ["Hive:AiGateway:Stub:Usage:OutputTokens"] = "8",
            ["Hive:AiGateway:Stub:Usage:TotalTokens"] = "21",
            ["Hive:AiGateway:Stub:Usage:IsEstimated"] = "true",
            ["Hive:AiGateway:Stub:Cost:Amount"] = "0.04",
            ["Hive:AiGateway:Stub:Cost:Currency"] = "EUR",
            ["Hive:AiGateway:Stub:Cost:IsEstimated"] = "true",
        });

        var gateway = provider.GetRequiredService<IAiGateway>();
        Assert.NotNull(provider.GetRequiredService<IAiGatewayAuditPublisher>());
        Assert.NotNull(provider.GetRequiredService<IAiGatewayDetailedAuditPublisher>());
        Assert.Null(provider.GetService<IChatClient>());

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal("Integration stub response.", response.Text);
        Assert.Equal(AiFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Provider);
        Assert.Equal("stub", response.Provider.ProviderId);
        Assert.Equal("integration-deterministic", response.Provider.ModelId);
        Assert.NotNull(response.Usage);
        Assert.Equal(13, response.Usage.InputTokens);
        Assert.Equal(8, response.Usage.OutputTokens);
        Assert.Equal(21, response.Usage.TotalTokens);
        Assert.True(response.Usage.IsEstimated);
        Assert.NotNull(response.Cost);
        Assert.Equal(0.04m, response.Cost.Amount);
        Assert.Equal("EUR", response.Cost.Currency);
        Assert.True(response.Cost.IsEstimated);
    }

    [Fact]
    public async Task Configured_stub_gateway_returns_tool_call_through_di()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Hive:AiGateway:Provider"] = "stub",
            ["Hive:AiGateway:Stub:Outcome"] = "tool-call",
            ["Hive:AiGateway:Stub:Text"] = "",
            ["Hive:AiGateway:Stub:ToolCall:Id"] = "call-integration-1",
            ["Hive:AiGateway:Stub:ToolCall:Name"] = "ticket.lookup",
            ["Hive:AiGateway:Stub:ToolCall:Arguments:ticket"] = "HIVE-789",
        });

        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(AiFinishReason.ToolCalls, response.FinishReason);
        Assert.Null(response.Text);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call-integration-1", toolCall.Id);
        Assert.Equal("ticket.lookup", toolCall.Name);
        Assert.Equal("HIVE-789", toolCall.Arguments["ticket"]);
        Assert.Null(provider.GetService<IChatClient>());
    }

    [Fact]
    public void Optional_real_smoke_settings_are_inert_without_complete_local_configuration()
    {
        var missingAll = OptionalRealSmokeSettings.From(_ => null);
        var missingModel = OptionalRealSmokeSettings.From(name =>
            name == RealTestApiKeyEnvironmentVariable ? "local-secret" : null);
        var missingKey = OptionalRealSmokeSettings.From(name =>
            name == RealTestModelIdEnvironmentVariable ? "local-model" : null);

        Assert.False(missingAll.IsEnabled);
        Assert.Empty(missingAll.ToConfiguration());
        Assert.False(missingModel.IsEnabled);
        Assert.Empty(missingModel.ToConfiguration());
        Assert.False(missingKey.IsEnabled);
        Assert.Empty(missingKey.ToConfiguration());
    }

    [Fact]
    public async Task Optional_real_provider_smoke_test_runs_only_with_local_secret_and_model()
    {
        var settings = OptionalRealSmokeSettings.FromEnvironment();
        if (!settings.IsEnabled)
        {
            Assert.Empty(settings.ToConfiguration());
            return;
        }

        using var provider = BuildProvider(settings.ToConfiguration());
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request(
            provider: new AiProviderMetadata("openai", settings.ModelId!),
            timeout: TimeSpan.FromSeconds(30)));

        Assert.True(response.IsSuccess || response.IsFailure);
        if (response.IsSuccess)
        {
            Assert.NotNull(response.Provider);
            Assert.Equal("openai", response.Provider.ProviderId);
            Assert.Equal(settings.ModelId, response.Provider.ModelId);
            Assert.DoesNotContain(settings.ApiKey!, response.Text ?? string.Empty);
            return;
        }

        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.NotNull(error.Provider);
        Assert.Equal("openai", error.Provider.ProviderId);
        Assert.Equal(settings.ModelId, error.Provider.ModelId);
        Assert.DoesNotContain(settings.ApiKey!, error.Message);
    }

    private static ServiceProvider BuildProvider(
        IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static AiGatewayRequest Request(
        AiProviderMetadata? provider = null,
        TimeSpan? timeout = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this integration smoke request in one short sentence.",
            provider: provider,
            timeout: timeout);

    private sealed record OptionalRealSmokeSettings(
        string? ApiKey,
        string? ModelId,
        string? Endpoint)
    {
        public bool IsEnabled =>
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(ModelId);

        public static OptionalRealSmokeSettings FromEnvironment() =>
            From(Environment.GetEnvironmentVariable);

        public static OptionalRealSmokeSettings From(
            Func<string, string?> readVariable) =>
            new(
                readVariable(RealTestApiKeyEnvironmentVariable),
                readVariable(RealTestModelIdEnvironmentVariable),
                readVariable(RealTestEndpointEnvironmentVariable));

        public IReadOnlyDictionary<string, string?> ToConfiguration()
        {
            if (!IsEnabled)
            {
                return new Dictionary<string, string?>();
            }

            var values = new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "real",
                ["Hive:AiGateway:Real:ProviderId"] = "openai",
                ["Hive:AiGateway:Real:ModelId"] = ModelId,
                ["Hive:AiGateway:Real:ApiKey"] = ApiKey,
            };

            if (!string.IsNullOrWhiteSpace(Endpoint))
            {
                values["Hive:AiGateway:Real:Endpoint"] = Endpoint;
            }

            return values;
        }
    }
}
