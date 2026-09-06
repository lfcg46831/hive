using System.Net;
using System.Text;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class MultiRealAiGatewayProviderTests
{
    [Fact]
    public async Task Versioned_profile_binds_prices_and_preserves_structured_output_and_tools_on_wire()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(MultiRealAiGatewaySmokeTests.FindRepositoryRoot(), "config", "ai-gateway.mistral.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:RealProviders:mistral:ApiKey"] = "mistral-secret",
            }).Build();
        var settings = Assert.Single(RoutingRealAiGatewayProvider.ReadAdditionalSettings(configuration));
        JsonElement body = default;
        using var router = new RoutingRealAiGatewayProvider(settings, [],
            (options, model) => RealAiChatClientFactory.Create(options, model, new Handler(async (request, ct) =>
            {
                body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                return Success(model);
            })));
        var baseline = Request("mistral");
        var request = new AiGatewayRequest(baseline.OrganizationId, baseline.PositionId, baseline.ThreadId,
            baseline.MessageId, "Return a JSON object.", provider: settings.DefaultProvider,
            tools: [new AiToolDefinition("lookup", "Look up a record.", new Dictionary<string, object?> { ["type"] = "object" })],
            outputConstraint: new AiOutputConstraint("answer", 1, JsonSerializer.SerializeToElement(new { type = "object" })));
        var response = await router.CompleteAsync(request, default);
        Assert.True(response.IsSuccess);
        Assert.Equal("mistral-2026-09-06", response.AppliedPricing!.Version);
        Assert.Equal(0.0000045m, response.Cost!.Amount);
        Assert.Equal("json_schema", body.GetProperty("response_format").GetProperty("type").GetString());
        Assert.Equal("answer", body.GetProperty("response_format").GetProperty("json_schema").GetProperty("name").GetString());
        Assert.Equal("lookup", body.GetProperty("tools")[0].GetProperty("function").GetProperty("name").GetString());
        Assert.Equal("mistral-small-2603", body.GetProperty("model").GetString());
    }

    [Fact]
    public async Task Concrete_transports_isolate_credentials_endpoints_models_and_token_limits()
    {
        var requests = new List<(string Host, string Key, JsonElement Body)>();
        using var router = new RoutingRealAiGatewayProvider(Settings("openai"), [Settings("mistral")],
            (settings, model) => RealAiChatClientFactory.Create(settings, model, new Handler(async (request, ct) =>
            {
                var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)).RootElement.Clone();
                requests.Add((request.RequestUri!.Host, request.Headers.Authorization!.Parameter!, body));
                Assert.Equal("/v1/chat/completions", request.RequestUri.AbsolutePath);
                return Success(body.GetProperty("model").GetString()!);
            })));

        foreach (var provider in new[] { "openai", "mistral", "openai" })
        {
            // The effective model deliberately differs from the host default.
            var response = await router.CompleteAsync(Request(provider, "effective-model"), default);
            Assert.True(response.IsSuccess);
            Assert.Equal(provider, response.Provider!.ProviderId);
            Assert.Equal("effective-model", response.Provider.ModelId);
            Assert.Equal(10, response.Usage!.InputTokens);
            Assert.Equal(5, response.Usage.OutputTokens);
            Assert.Equal(provider + "-test-v1", response.AppliedPricing!.Version);
            Assert.Equal(provider == "mistral" ? 0.0045m : 0.002m, response.Cost!.Amount);
        }

        Assert.Equal(new[] { "api.openai.com", "api.mistral.ai", "api.openai.com" }, requests.Select(x => x.Host));
        Assert.Equal(new[] { "openai-secret", "mistral-secret", "openai-secret" }, requests.Select(x => x.Key));
        Assert.Equal(64, requests[0].Body.GetProperty("max_completion_tokens").GetInt32());
        Assert.Equal(64, requests[1].Body.GetProperty("max_tokens").GetInt32());
        Assert.False(requests[1].Body.TryGetProperty("max_completion_tokens", out _));
        Assert.Equal("system", requests[1].Body.GetProperty("messages")[0].GetProperty("role").GetString());
    }

    [Theory]
    [InlineData(401, AiGatewayErrorCode.CredentialsMissing, false)]
    [InlineData(403, AiGatewayErrorCode.CredentialsMissing, false)]
    [InlineData(408, AiGatewayErrorCode.Timeout, true)]
    [InlineData(429, AiGatewayErrorCode.QuotaExceeded, true)]
    [InlineData(422, AiGatewayErrorCode.ProviderRejected, false)]
    [InlineData(500, AiGatewayErrorCode.ProviderUnavailable, true)]
    [InlineData(503, AiGatewayErrorCode.ProviderUnavailable, true)]
    public async Task Mistral_errors_are_canonical_sanitized_and_never_retried_by_sdk(
        int status, AiGatewayErrorCode code, bool retryable)
    {
        var calls = 0;
        using var router = new RoutingRealAiGatewayProvider(Settings("mistral"), [],
            (settings, model) => RealAiChatClientFactory.Create(settings, model, new Handler((_, _) =>
            {
                calls++;
                return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status)
                {
                    Content = new StringContent("{\"message\":\"mistral-secret private-prompt\"}"),
                });
            })));
        var response = await router.CompleteAsync(Request("mistral"), default);
        Assert.Equal(code, response.Error!.Code);
        Assert.Equal(retryable, response.Error.IsRetryable);
        Assert.Equal(status, response.Error.Diagnostics!.ProviderStatusCode);
        Assert.DoesNotContain("secret", response.Error.Message);
        Assert.DoesNotContain("private-prompt", response.Error.Message);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task Unknown_provider_fails_closed_without_constructing_a_client()
    {
        using var router = new RoutingRealAiGatewayProvider(Settings("openai"), [],
            (_, _) => throw new InvalidOperationException("Must not construct transport"));
        var response = await router.CompleteAsync(Request("mistral"), default);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, response.Error!.Code);
        Assert.False(response.Error.IsRetryable);
    }

    [Fact]
    public async Task Caller_cancellation_reaches_the_mistral_transport()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cts = new CancellationTokenSource();
        using var router = new RoutingRealAiGatewayProvider(Settings("mistral"), [],
            (settings, model) => RealAiChatClientFactory.Create(settings, model, new Handler(async (_, ct) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, ct);
                return Success(model);
            })));
        var pending = router.CompleteAsync(Request("mistral"), cts.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
    }

    [Fact]
    public async Task Fallback_crosses_concrete_transports_and_audits_each_attempt()
    {
        var calls = new List<string>();
        using var router = new RoutingRealAiGatewayProvider(Settings("openai"), [Settings("mistral")],
            (settings, model) => RealAiChatClientFactory.Create(settings, model, new Handler((_, _) =>
            {
                calls.Add(settings.DefaultProvider.ProviderId);
                return Task.FromResult(settings.DefaultProvider.ProviderId == "openai"
                    ? new HttpResponseMessage(HttpStatusCode.TooManyRequests) { Content = new StringContent("{}") }
                    : Success(model));
            })));
        var audit = new RecordingAudit();
        var resolver = new OneAttemptPolicy();
        using var limiter = new AiProviderAdmissionLimiter(resolver);
        var gateway = new AiGateway(router, audit, admissionLimiter: limiter, resiliencePolicyResolver: resolver);
        var request = Request("openai", fallback: "mistral");
        var response = await gateway.CompleteAsync(request);
        Assert.True(response.IsSuccess);
        Assert.Equal(new[] { "openai", "mistral" }, calls);
        Assert.Equal("mistral", response.Provider!.ProviderId);
        var attempts = audit.Events.Where(x => x.Scope == AiGatewayCostAuditScope.Attempt).ToArray();
        Assert.Equal(new[] { "openai", "mistral" }, attempts.Select(x => x.Provider!.ProviderId));
        Assert.Equal(new int?[] { 0, 1 }, attempts.Select(x => x.CandidateIndex));
        Assert.Equal(2, attempts.Select(x => x.AttemptId).Distinct().Count());
        Assert.All(attempts, x => Assert.Equal(request.MessageId, x.MessageId));
        Assert.Equal("mistral-test-v1", attempts[1].AppliedPricing!.Version);
        Assert.Single(audit.Events.Where(x => x.Scope == AiGatewayCostAuditScope.Journey));
    }

    [Fact]
    public async Task Mistral_tool_calls_are_normalized_without_executing_tools()
    {
        using var router = new RoutingRealAiGatewayProvider(Settings("mistral"), [],
            (settings, model) => RealAiChatClientFactory.Create(settings, model, new Handler((_, _) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("""
                    {"id":"test","object":"chat.completion","created":1,"model":"default-model",
                     "choices":[{"index":0,"finish_reason":"tool_calls","message":{"role":"assistant","content":null,
                       "tool_calls":[{"id":"call12345","type":"function","function":{"name":"lookup","arguments":"{\"id\":1}"}}]}}],
                     "usage":{"prompt_tokens":10,"completion_tokens":5,"total_tokens":15}}
                    """, Encoding.UTF8, "application/json"),
                }))));
        var result = await router.CompleteAsync(Request("mistral"), default);
        Assert.True(result.IsSuccess);
        var tool = Assert.Single(result.ToolCalls);
        Assert.Equal("lookup", tool.Name);
        Assert.Equal("call12345", tool.Id);
    }

    [Theory]
    [InlineData("ModelId", null)]
    [InlineData("ApiKey", null)]
    [InlineData("ProviderId", "openai")]
    [InlineData("Endpoint", "invalid-secret")]
    [InlineData("Temperature", "invalid-secret")]
    [InlineData("Unknown", "invalid-secret")]
    public void Additional_configuration_fails_sanitized_without_inheriting_defaults(string field, string? value)
    {
        var values = Configuration();
        values["Hive:AiGateway:RealProviders:mistral:" + field] = value;
        var services = new ServiceCollection();
        services.AddHiveAiGateway(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var provider = services.BuildServiceProvider();
        var exception = Assert.Throws<InvalidOperationException>(() => provider.GetRequiredService<IAiGatewayProvider>());
        Assert.DoesNotContain("secret", exception.Message);
    }

    [Fact]
    public void Configuration_registers_both_providers_without_network_and_rejects_duplicate_default()
    {
        var values = Configuration();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);
        using var provider = services.BuildServiceProvider();
        Assert.IsType<RoutingRealAiGatewayProvider>(provider.GetRequiredService<IAiGatewayProvider>());
        values["Hive:AiGateway:Real:ProviderId"] = "mistral";
        var duplicate = new ServiceCollection();
        duplicate.AddHiveAiGateway(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        using var invalid = duplicate.BuildServiceProvider();
        Assert.Throws<InvalidOperationException>(() => invalid.GetRequiredService<IAiGatewayProvider>());
    }

    internal static Dictionary<string, string?> Configuration() => new()
    {
        ["Hive:AiGateway:Provider"] = "real",
        ["Hive:AiGateway:Real:ProviderId"] = "openai",
        ["Hive:AiGateway:Real:ModelId"] = "default-model",
        ["Hive:AiGateway:Real:ApiKey"] = "openai-secret",
        ["Hive:AiGateway:RealProviders:mistral:ModelId"] = "default-model",
        ["Hive:AiGateway:RealProviders:mistral:ApiKey"] = "mistral-secret",
    };

    internal static RealAiGatewayProviderSettings Settings(string provider) => new(
        provider + "-secret", new AiProviderMetadata(provider, "default-model"), AiModelParameters.Default,
        pricingCatalog: new AiPricingCatalog(provider + "-test-v1", 1000,
            [new AiModelPricing(provider, "default-model", ["effective-model"],
                provider == "mistral" ? 0.15m : 0.1m, provider == "mistral" ? 0.6m : 0.2m, "USD")]));

    internal static AiGatewayRequest Request(string provider, string model = "default-model", string? fallback = null) => new(
        OrganizationId.From("smoke-org"), PositionId.From("smoke-position"), ThreadId.From(Guid.NewGuid()),
        MessageId.From(Guid.NewGuid()), "Reply with OK.", systemInstruction: "Be concise.",
        provider: new AiProviderMetadata(provider, model), modelParameters: new AiModelParameters(maxOutputTokens: 64),
        timeout: TimeSpan.FromSeconds(30),
        policy: fallback is null ? null : new AiGatewayPolicy(
            [new AiProviderMetadata(provider, model), new AiProviderMetadata(fallback, model)],
            true, null, null, null, null, [new AiProviderMetadata(fallback, model)]));

    private static HttpResponseMessage Success(string model) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(new
        {
            id = "test", @object = "chat.completion", created = 1, model,
            choices = new[] { new { index = 0, finish_reason = "stop", message = new { role = "assistant", content = "OK" } } },
            usage = new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 15 },
        }), Encoding.UTF8, "application/json"),
    };

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            send(request, cancellationToken);
    }

    internal sealed class RecordingAudit : IAiGatewayAuditPublisher
    {
        public List<AiGatewayCostAuditEvent> Events { get; } = [];
        public void Publish(AiGatewayCostAuditEvent @event) => Events.Add(@event);
    }

    internal sealed class OneAttemptPolicy : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => new(
            AiProviderRateLimitPolicy.Default, AiProviderQueuePolicy.Default,
            new AiProviderRetryPolicy(1, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0),
            AiProviderCircuitBreakerPolicy.Default);
    }
}
