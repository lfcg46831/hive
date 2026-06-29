using Hive.Domain.Ai;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class RealAiGatewayProviderConfigurationTests
{
    private static IRealAiGatewayProviderFactory Factory(
        Action<RealAiGatewayProviderOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayRealConfiguration(configure);
        return services
            .BuildServiceProvider()
            .GetRequiredService<IRealAiGatewayProviderFactory>();
    }

    private static RealAiGatewayProviderSettings ValidSettings() =>
        new(
            "secret-key",
            new AiProviderMetadata("openai", "gpt-4o-mini"),
            new AiModelParameters(0.5m, 256),
            new Uri("https://api.example.com/v1"),
            TimeSpan.FromSeconds(30));

    [Fact]
    public void Binds_configuration_section_into_validated_settings()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Real:ProviderId"] = "openai",
                ["Hive:AiGateway:Real:ModelId"] = "gpt-4o-mini",
                ["Hive:AiGateway:Real:Endpoint"] = "https://api.example.com/v1",
                ["Hive:AiGateway:Real:ApiKey"] = "secret-key",
                ["Hive:AiGateway:Real:Temperature"] = "0.5",
                ["Hive:AiGateway:Real:MaxOutputTokens"] = "256",
                ["Hive:AiGateway:Real:TimeoutSeconds"] = "30",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddHiveAiGatewayRealConfiguration(configuration);
        var factory = services
            .BuildServiceProvider()
            .GetRequiredService<IRealAiGatewayProviderFactory>();

        var result = factory.ResolveSettings();

        Assert.True(result.IsSuccess);
        var settings = result.Settings!;
        Assert.Equal("openai", settings.DefaultProvider.ProviderId);
        Assert.Equal("gpt-4o-mini", settings.DefaultProvider.ModelId);
        Assert.Equal(new Uri("https://api.example.com/v1"), settings.Endpoint);
        Assert.Equal("secret-key", settings.ApiKey);
        Assert.Equal(0.5m, settings.DefaultParameters.Temperature);
        Assert.Equal(256, settings.DefaultParameters.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(30), settings.Timeout);
    }

    [Fact]
    public void Missing_api_key_fails_with_credentials_missing()
    {
        var result = Factory(options =>
        {
            options.ProviderId = "openai";
            options.ModelId = "gpt-4o-mini";
        }).ResolveSettings();

        Assert.True(result.IsFailure);
        Assert.Null(result.Settings);
        Assert.Equal(AiGatewayErrorCode.CredentialsMissing, result.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(result.FailureReason));
    }

    [Theory]
    [InlineData(null, "gpt-4o-mini")]
    [InlineData("openai", null)]
    [InlineData("   ", "gpt-4o-mini")]
    [InlineData("openai", "   ")]
    public void Missing_provider_or_model_fails_with_configuration_invalid(
        string? providerId,
        string? modelId)
    {
        var result = Factory(options =>
        {
            options.ProviderId = providerId;
            options.ModelId = modelId;
            options.ApiKey = "secret-key";
        }).ResolveSettings();

        Assert.True(result.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, result.ErrorCode);
    }

    [Fact]
    public void Out_of_range_temperature_fails_with_configuration_invalid()
    {
        var result = Factory(options =>
        {
            options.ProviderId = "openai";
            options.ModelId = "gpt-4o-mini";
            options.ApiKey = "secret-key";
            options.Temperature = 5m;
        }).ResolveSettings();

        Assert.True(result.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, result.ErrorCode);
    }

    [Fact]
    public void Non_positive_max_output_tokens_fails_with_configuration_invalid()
    {
        var result = Factory(options =>
        {
            options.ProviderId = "openai";
            options.ModelId = "gpt-4o-mini";
            options.ApiKey = "secret-key";
            options.MaxOutputTokens = 0;
        }).ResolveSettings();

        Assert.True(result.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, result.ErrorCode);
    }

    [Fact]
    public void Relative_endpoint_fails_with_configuration_invalid()
    {
        var result = Factory(options =>
        {
            options.ProviderId = "openai";
            options.ModelId = "gpt-4o-mini";
            options.ApiKey = "secret-key";
            options.Endpoint = "/relative/path";
        }).ResolveSettings();

        Assert.True(result.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, result.ErrorCode);
    }

    [Fact]
    public void Non_positive_timeout_fails_with_configuration_invalid()
    {
        var result = Factory(options =>
        {
            options.ProviderId = "openai";
            options.ModelId = "gpt-4o-mini";
            options.ApiKey = "secret-key";
            options.TimeoutSeconds = 0;
        }).ResolveSettings();

        Assert.True(result.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, result.ErrorCode);
    }

    [Fact]
    public void Resolve_without_position_uses_configured_defaults()
    {
        var settings = ValidSettings();

        var effective = settings.Resolve(null);

        Assert.Equal("openai", effective.Provider.ProviderId);
        Assert.Equal("gpt-4o-mini", effective.Provider.ModelId);
        Assert.Equal(0.5m, effective.Parameters.Temperature);
        Assert.Equal(256, effective.Parameters.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(30), effective.Timeout);
    }

    [Fact]
    public void Resolve_with_position_overrides_defaults_and_keeps_secret_separate()
    {
        var settings = ValidSettings();
        var position = new AiPositionRuntimeConfiguration(
            new AiProviderMetadata("anthropic", "claude-haiku"),
            new AiModelParameters(temperature: 1.0m),
            timeout: TimeSpan.FromSeconds(10));

        var effective = settings.Resolve(position);

        Assert.Equal("anthropic", effective.Provider.ProviderId);
        Assert.Equal("claude-haiku", effective.Provider.ModelId);
        Assert.Equal(1.0m, effective.Parameters.Temperature);
        // Position omits max output tokens: falls back to the configured default.
        Assert.Equal(256, effective.Parameters.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(10), effective.Timeout);
        // The secret never crosses into the resolved model and stays in settings.
        Assert.Equal("secret-key", settings.ApiKey);
    }

    [Fact]
    public void Resolve_with_position_falls_back_to_default_timeout_when_absent()
    {
        var settings = ValidSettings();
        var position = new AiPositionRuntimeConfiguration(
            new AiProviderMetadata("anthropic", "claude-haiku"));

        var effective = settings.Resolve(position);

        Assert.Equal(TimeSpan.FromSeconds(30), effective.Timeout);
    }

    [Fact]
    public void ToString_redacts_the_secret()
    {
        var rendered = ValidSettings().ToString();

        Assert.DoesNotContain("secret-key", rendered);
        Assert.Contains("***redacted***", rendered);
    }

    [Fact]
    public void Effective_model_does_not_expose_a_secret_member()
    {
        var members = typeof(RealAiGatewayEffectiveModel).GetProperties();

        Assert.DoesNotContain(members, property =>
            property.Name.Contains("ApiKey", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
            property.Name.Contains("Credential", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Registration_adds_the_factory()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGatewayRealConfiguration();

        using var provider = services.BuildServiceProvider();

        Assert.NotNull(provider.GetService<IRealAiGatewayProviderFactory>());
    }
}
