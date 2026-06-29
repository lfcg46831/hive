using Hive.Domain.Ai;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Ai;

internal sealed class RealAiGatewayProviderFactory : IRealAiGatewayProviderFactory
{
    private readonly IOptions<RealAiGatewayProviderOptions> _options;

    public RealAiGatewayProviderFactory(IOptions<RealAiGatewayProviderOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public RealAiGatewayProviderConfigurationResult ResolveSettings()
    {
        var options = _options.Value ?? new RealAiGatewayProviderOptions();

        var providerId = Trimmed(options.ProviderId);
        var modelId = Trimmed(options.ModelId);
        if (providerId is null || modelId is null)
        {
            return RealAiGatewayProviderConfigurationResult.Failure(
                AiGatewayErrorCode.ConfigurationInvalid,
                "AI gateway real provider requires both 'ProviderId' and 'ModelId'.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            return RealAiGatewayProviderConfigurationResult.Failure(
                AiGatewayErrorCode.CredentialsMissing,
                "AI gateway real provider requires a configured 'ApiKey'.");
        }

        AiModelParameters parameters;
        try
        {
            parameters = new AiModelParameters(options.Temperature, options.MaxOutputTokens);
        }
        catch (ArgumentOutOfRangeException ex)
        {
            return RealAiGatewayProviderConfigurationResult.Failure(
                AiGatewayErrorCode.ConfigurationInvalid,
                $"AI gateway real provider has invalid model parameters: {ex.Message}");
        }

        Uri? endpoint = null;
        var endpointText = Trimmed(options.Endpoint);
        if (endpointText is not null &&
            !Uri.TryCreate(endpointText, UriKind.Absolute, out endpoint))
        {
            return RealAiGatewayProviderConfigurationResult.Failure(
                AiGatewayErrorCode.ConfigurationInvalid,
                "AI gateway real provider 'Endpoint' must be an absolute URI.");
        }

        TimeSpan? timeout = null;
        if (options.TimeoutSeconds is { } seconds)
        {
            if (seconds <= 0)
            {
                return RealAiGatewayProviderConfigurationResult.Failure(
                    AiGatewayErrorCode.ConfigurationInvalid,
                    "AI gateway real provider 'TimeoutSeconds' must be greater than zero.");
            }

            timeout = TimeSpan.FromSeconds(seconds);
        }

        var settings = new RealAiGatewayProviderSettings(
            options.ApiKey,
            new AiProviderMetadata(providerId, modelId),
            parameters,
            endpoint,
            timeout);

        return RealAiGatewayProviderConfigurationResult.Success(settings);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
