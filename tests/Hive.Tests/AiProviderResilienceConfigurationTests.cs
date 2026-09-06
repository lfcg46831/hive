using System.Text;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class AiProviderResilienceConfigurationTests
{
    private const string Root = AiProviderResilienceOptions.SectionName;

    [Fact]
    public async Task Absent_configuration_starts_with_defaults_including_legacy_bucket()
    {
        using var host = BuildHost(Config());
        await host.StartAsync();
        var resolver = host.Services.GetRequiredService<IAiProviderResiliencePolicyResolver>();
        Assert.Same(AiProviderResiliencePolicy.Default, resolver.Resolve(null));
        Assert.Same(AiProviderResiliencePolicy.Default, resolver.Resolve(new("other", "model")));
        await host.StopAsync();
    }

    [Fact]
    public async Task Json_overrides_every_field_and_materializes_independent_immutable_policies()
    {
        using var json = new MemoryStream(Encoding.UTF8.GetBytes("""
            { "Hive": { "AiGateway": { "Providers": {
              "alpha": {
                "RateLimit": { "MaxConcurrentCalls": 2, "MaxCallsPerWindow": 17, "Window": "00:00:12" },
                "Queue": { "MaxDepth": 0, "MaxWait": "00:00:00" },
                "Retry": { "MaxAttempts": 7, "InitialBackoff": "00:00:00.100", "MaxBackoff": "00:00:02", "JitterRatio": 0.4 },
                "CircuitBreaker": { "SamplingWindow": "00:00:20", "FailureThreshold": 9, "OpenDuration": "00:00:15", "HalfOpenMaxConcurrentProbes": 2 }
              },
              "beta": { "RateLimit": { "MaxConcurrentCalls": 6 } },
              "empty": {}
            } } } }
            """));
        var configuration = new ConfigurationBuilder().AddJsonStream(json).Build();
        using var host = BuildHost(configuration);
        await host.StartAsync();
        var resolver = host.Services.GetRequiredService<IAiProviderResiliencePolicyResolver>();
        var alpha = resolver.Resolve(new("alpha", "one"));
        Assert.Equal(new AiProviderResiliencePolicy(
            new(2, 17, TimeSpan.FromSeconds(12)),
            new(0, TimeSpan.Zero),
            new(7, TimeSpan.FromMilliseconds(100), TimeSpan.FromSeconds(2), 0.4m),
            new(TimeSpan.FromSeconds(20), 9, TimeSpan.FromSeconds(15), 2)), alpha);
        Assert.Same(alpha, resolver.Resolve(new("alpha", "two")));
        var beta = resolver.Resolve(new("beta", "one"));
        Assert.Equal(6, beta.RateLimit.MaxConcurrentCalls);
        Assert.Equal(AiProviderQueuePolicy.Default, beta.Queue);
        Assert.Equal(AiProviderRetryPolicy.Default, beta.Retry);
        Assert.Equal(AiProviderCircuitBreakerPolicy.Default, beta.CircuitBreaker);
        Assert.Equal(AiProviderResiliencePolicy.Default, resolver.Resolve(new("empty", "one")));
        Assert.Same(AiProviderResiliencePolicy.Default, resolver.Resolve(new("ALPHA", "one")));
        await host.StopAsync();
    }

    [Fact]
    public async Task Environment_overrides_base_configuration_and_reload_preserves_startup_snapshot()
    {
        var prefix = $"HIVE_RESILIENCE_TEST_{Guid.NewGuid():N}_";
        var key = prefix + "Hive__AiGateway__Providers__alpha__Retry__MaxAttempts";
        try
        {
            Environment.SetEnvironmentVariable(key, "2");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    [$"{Root}:alpha:Retry:MaxAttempts"] = "8"
                })
                .AddEnvironmentVariables(prefix)
                .Build();
            using var host = BuildHost(configuration);
            await host.StartAsync();
            // Reload before first resolver access also preserves the validated startup snapshot.
            Environment.SetEnvironmentVariable(key, "4");
            configuration.Reload();
            var resolver = host.Services.GetRequiredService<IAiProviderResiliencePolicyResolver>();
            Assert.Equal(2, resolver.Resolve(new("alpha", "one")).Retry.MaxAttempts);
            await host.StopAsync();
        }
        finally
        {
            Environment.SetEnvironmentVariable(key, null);
        }
    }

    [Theory]
    [InlineData("RateLimit:MaxConcurrentCalls", "0")]
    [InlineData("RateLimit:MaxConcurrentCalls", "2147483648")]
    [InlineData("RateLimit:MaxCallsPerWindow", "-1")]
    [InlineData("RateLimit:Window", "00:00:00")]
    [InlineData("RateLimit:Window", "secret-value")]
    [InlineData("Queue:MaxDepth", "0")]
    [InlineData("Queue:MaxDepth", "-1")]
    [InlineData("Queue:MaxWait", "00:00:00")]
    [InlineData("Queue:MaxWait", "50.00:00:00")]
    [InlineData("Retry:MaxAttempts", "0")]
    [InlineData("Retry:MaxAttempts", "")]
    [InlineData("Retry:MaxAttempts", null)]
    [InlineData("Retry:InitialBackoff", "00:00:06")]
    [InlineData("Retry:InitialBackoff", "00:00:00")]
    [InlineData("Retry:MaxBackoff", "00:00:00.001")]
    [InlineData("Retry:MaxBackoff", "50.00:00:00")]
    [InlineData("Retry:JitterRatio", "1.01")]
    [InlineData("Retry:JitterRatio", "-0.01")]
    [InlineData("Retry:JitterRatio", "NaN")]
    [InlineData("Retry:JitterRatio", "0,2")]
    [InlineData("CircuitBreaker:SamplingWindow", "00:00:00")]
    [InlineData("CircuitBreaker:FailureThreshold", "0")]
    [InlineData("CircuitBreaker:OpenDuration", "-00:00:01")]
    [InlineData("CircuitBreaker:HalfOpenMaxConcurrentProbes", "0")]
    [InlineData("Retry:secret-field", "secret-value")]
    [InlineData("secret-field", "secret-value")]
    [InlineData("Retry", "secret-value")]
    [InlineData("Queue", "secret-value")]
    [InlineData("RateLimit", "secret-value")]
    [InlineData("CircuitBreaker", "secret-value")]
    [InlineData("Retry:MaxAttempts:secret-field", "secret-value")]
    public async Task Invalid_inactive_provider_fails_at_startup_without_echoing_input(
        string field, string? value)
    {
        using var host = BuildHost(Config(($"secret-provider:{field}", value)));
        var exception = await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync());
        AssertSanitized(exception);
    }

    [Theory]
    [InlineData("", "secret-value")]
    [InlineData(":secret-provider", "secret-value")]
    [InlineData(": secret-provider :Retry:MaxAttempts", "2")]
    public async Task Invalid_root_or_provider_shape_is_sanitized(string suffix, string value)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?> { [Root + suffix] = value }).Build();
        using var host = BuildHost(configuration);
        AssertSanitized(await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync()));
    }

    [Fact]
    public async Task Common_bootstrap_and_direct_real_activation_validate_without_provider_resolution()
    {
        foreach (var commonBootstrap in new[] { true, false })
        {
            var builder = Host.CreateApplicationBuilder();
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:Node:Roles:0"] = "api",
                [$"{Root}:secret-provider:Retry:MaxAttempts"] = "0"
            });
            if (commonBootstrap) builder.AddHiveBootstrap();
            else builder.Services.AddHiveAiGatewayReal(builder.Configuration);
            using var host = builder.Build();
            AssertSanitized(await Assert.ThrowsAsync<OptionsValidationException>(() => host.StartAsync()));
        }
    }

    [Fact]
    public async Task Explicit_resolver_is_preserved_but_does_not_bypass_configuration_validation()
    {
        var custom = new CustomResolver();
        using var host = BuildHost(Config(), services =>
            services.AddSingleton<IAiProviderResiliencePolicyResolver>(custom));
        await host.StartAsync();
        Assert.Same(custom, host.Services.GetRequiredService<IAiProviderResiliencePolicyResolver>());
        await host.StopAsync();

        using var invalid = BuildHost(Config(("alpha:Retry:MaxAttempts", "0")), services =>
            services.AddSingleton<IAiProviderResiliencePolicyResolver>(custom));
        AssertSanitized(await Assert.ThrowsAsync<OptionsValidationException>(() => invalid.StartAsync()));
    }

    [Fact]
    public async Task Configured_queue_enforces_concurrency_depth_and_provider_isolation_in_pipeline()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var host = BuildHost(Config(
            ("alpha:RateLimit:MaxConcurrentCalls", "1"),
            ("alpha:Queue:MaxDepth", "1"),
            ("alpha:Retry:MaxAttempts", "1")), services =>
            services.AddSingleton<IAiGatewayProvider>(new DelegateProvider(async (request, token) =>
            {
                if (request.Provider!.ProviderId == "alpha")
                {
                    entered.TrySetResult();
                    await release.Task.WaitAsync(token);
                }
                return Success(request);
            })));
        await host.StartAsync();
        var gateway = host.Services.GetRequiredService<IAiGateway>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var first = gateway.CompleteAsync(Request("alpha"), timeout.Token);
        await entered.Task.WaitAsync(timeout.Token);
        var queued = gateway.CompleteAsync(Request("alpha", "other-model"), timeout.Token);
        try
        {
            Assert.False(queued.IsCompleted);
            var overflow = await gateway.CompleteAsync(Request("alpha"), timeout.Token);
            Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, overflow.Error!.Code);
            Assert.True((await gateway.CompleteAsync(Request("beta"), timeout.Token)).IsSuccess);
        }
        finally
        {
            release.TrySetResult();
        }
        Assert.True((await first).IsSuccess);
        Assert.True((await queued).IsSuccess);
        await host.StopAsync();
    }

    [Fact]
    public async Task Configured_retry_and_circuit_threshold_control_provider_attempts()
    {
        var calls = 0;
        using var host = BuildHost(Config(
            ("alpha:Retry:MaxAttempts", "2"),
            ("alpha:Retry:InitialBackoff", "00:00:00.001"),
            ("alpha:Retry:MaxBackoff", "00:00:00.001"),
            ("alpha:Retry:JitterRatio", "0"),
            ("alpha:CircuitBreaker:FailureThreshold", "2")), services =>
            services.AddSingleton<IAiGatewayProvider>(new DelegateProvider((request, _) =>
            {
                calls++;
                return Task.FromResult(AiGatewayResponse.Failed(new AiGatewayError(
                    request.OrganizationId, request.PositionId, request.ThreadId, request.MessageId,
                    AiGatewayErrorCode.Timeout, "Provider timed out.", true, request.Provider)));
            })));
        await host.StartAsync();
        var gateway = host.Services.GetRequiredService<IAiGateway>();
        var failed = await gateway.CompleteAsync(Request("alpha"));
        Assert.Equal(AiGatewayErrorCode.Timeout, failed.Error!.Code);
        Assert.Equal(2, calls);
        var open = await gateway.CompleteAsync(Request("alpha", "other-model"));
        Assert.Equal(AiGatewayErrorReason.CircuitOpen, open.Error!.Reason);
        Assert.Equal(2, calls);
        await host.StopAsync();
    }

    private static void AssertSanitized(OptionsValidationException exception)
    {
        Assert.Contains("configuration-invalid", exception.Message);
        Assert.Contains(Root, exception.Message);
        Assert.DoesNotContain("secret-", exception.ToString());
        Assert.Null(exception.InnerException);
        Assert.Single(exception.Failures);
    }

    private static IConfigurationRoot Config(params (string Key, string? Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(
            item => $"{Root}:{item.Key}", item => item.Value)).Build();

    private static IHost BuildHost(IConfiguration configuration, Action<IServiceCollection>? register = null)
    {
        var builder = Host.CreateApplicationBuilder();
        register?.Invoke(builder.Services);
        builder.Services.AddHiveAiGateway(configuration);
        return builder.Build();
    }

    private static AiGatewayRequest Request(string provider, string model = "model") => new(
        OrganizationId.From("acme-delivery"), PositionId.From("triage-agent"),
        ThreadId.From(Guid.NewGuid()), MessageId.From(Guid.NewGuid()), "Classify this bug.",
        provider: new AiProviderMetadata(provider, model));

    private static AiGatewayResponse Success(AiGatewayRequest request) => AiGatewayResponse.Succeeded(
        request.OrganizationId, request.PositionId, request.ThreadId, request.MessageId,
        "Done.", AiFinishReason.Stop, request.Provider);

    private sealed class CustomResolver : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) =>
            AiProviderResiliencePolicy.Default;
    }

    private sealed class DelegateProvider(
        Func<AiGatewayRequest, CancellationToken, Task<AiGatewayResponse>> complete) : IAiGatewayProvider
    {
        public Task<AiGatewayResponse> CompleteAsync(AiGatewayRequest request,
            CancellationToken cancellationToken = default) => complete(request, cancellationToken);
    }
}
