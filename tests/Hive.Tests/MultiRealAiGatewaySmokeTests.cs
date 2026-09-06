using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit.Abstractions;

namespace Hive.Tests;

public sealed class MultiRealAiGatewaySmokeTests(ITestOutputHelper output)
{
    [MultiProviderSmokeFact]
    [Trait("Category", "RealProviderSmoke")]
    public async Task Real_providers_succeed_directly_and_through_fallback_in_both_directions()
    {
        var openAiKey = Required("HIVE_AI_GATEWAY_REAL_TEST_API_KEY");
        var openAiModel = Required("HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID");
        var mistralKey = Required("HIVE_AI_GATEWAY_MISTRAL_TEST_API_KEY");
        var root = FindRepositoryRoot();
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(root, "config", "ai-gateway.mistral.json"))
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "real",
                ["Hive:AiGateway:Real:ProviderId"] = "openai",
                ["Hive:AiGateway:Real:ModelId"] = openAiModel,
                ["Hive:AiGateway:Real:ApiKey"] = openAiKey,
                ["Hive:AiGateway:Real:TimeoutSeconds"] = "45",
                ["Hive:AiGateway:RealProviders:mistral:ApiKey"] = mistralKey,
            }).Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);
        using var provider = services.BuildServiceProvider();
        var real = provider.GetRequiredService<IAiGatewayProvider>();
        var models = new[]
        {
            new AiProviderMetadata("openai", openAiModel),
            new AiProviderMetadata("mistral", "mistral-small-2603"),
        };

        foreach (var model in models)
        {
            var response = await provider.GetRequiredService<IAiGateway>().CompleteAsync(Request(model));
            RequireSuccess(response, model.ProviderId);
            if (model.ProviderId == "mistral") Assert.Equal("mistral-2026-09-06", response.AppliedPricing?.Version);
            output.WriteLine($"direct provider={model.ProviderId}; result=succeeded; usage=available");
        }

        foreach (var primary in models)
        {
            var secondary = models.Single(x => x != primary);
            var audit = new MultiRealAiGatewayProviderTests.RecordingAudit();
            var resolver = new MultiRealAiGatewayProviderTests.OneAttemptPolicy();
            using var limiter = new AiProviderAdmissionLimiter(resolver);
            var gateway = new AiGateway(new InjectPrimaryFailure(real, primary.ProviderId), audit,
                admissionLimiter: limiter, resiliencePolicyResolver: resolver);
            var response = await gateway.CompleteAsync(Request(primary, secondary));
            RequireSuccess(response, secondary.ProviderId);
            var attempts = audit.Events.Where(x => x.Scope == AiGatewayCostAuditScope.Attempt).ToArray();
            Assert.Equal(new[] { primary.ProviderId, secondary.ProviderId }, attempts.Select(x => x.Provider!.ProviderId));
            Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, attempts[0].ErrorCode);
            Assert.Equal(AiGatewayCallResult.Succeeded, attempts[1].Result);
            output.WriteLine($"fallback={primary.ProviderId}->{secondary.ProviderId}; primary=injected-unavailable; secondary=real-success");
        }
    }

    private static void RequireSuccess(AiGatewayResponse response, string providerId)
    {
        // Keep provider payload, prompts and credentials out of test failure diagnostics.
        Assert.True(response.IsSuccess, "Real smoke failed: " +
            (response.Error is { } error ? AiGatewayErrorCodeContract.ToWireValue(error.Code) : "invalid-response"));
        Assert.Equal(providerId, response.Provider?.ProviderId);
        Assert.NotNull(response.Usage?.InputTokens);
        Assert.NotNull(response.Usage?.OutputTokens);
        Assert.False(string.IsNullOrWhiteSpace(response.Text));
    }

    private static AiGatewayRequest Request(AiProviderMetadata primary, AiProviderMetadata? secondary = null) => new(
        OrganizationId.From("real-smoke"), PositionId.From("probe"), ThreadId.From(Guid.NewGuid()),
        MessageId.From(Guid.NewGuid()), "Reply with the word OK.", provider: primary,
        modelParameters: new AiModelParameters(maxOutputTokens: 256), timeout: TimeSpan.FromSeconds(45),
        policy: secondary is null ? null : new AiGatewayPolicy(
            [primary, secondary], true, null, null, null, null, [secondary]));

    private static string Required(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        Assert.False(string.IsNullOrWhiteSpace(value), "Missing smoke configuration: " + name);
        return value!;
    }

    internal static string FindRepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Hive.sln"))) return directory.FullName;
        }
        throw new InvalidOperationException("Repository root not found.");
    }

    private sealed class InjectPrimaryFailure(IAiGatewayProvider inner, string primary) : IAiGatewayProvider
    {
        public Task<AiGatewayResponse> CompleteAsync(AiGatewayRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return request.Provider?.ProviderId == primary
                ? Task.FromResult(AiGatewayResponse.Failed(new AiGatewayError(
                    request.OrganizationId, request.PositionId, request.ThreadId, request.MessageId,
                    AiGatewayErrorCode.ProviderUnavailable, "Injected smoke failure before transport.", true, request.Provider)))
                : inner.CompleteAsync(request, cancellationToken);
        }
    }
}

public sealed class MultiProviderSmokeFactAttribute : FactAttribute
{
    public MultiProviderSmokeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("HIVE_AI_GATEWAY_MULTI_REAL_SMOKE") != "1")
            Skip = "Real multi-provider smoke is opt-in; set HIVE_AI_GATEWAY_MULTI_REAL_SMOKE=1 and both credentials.";
    }
}
