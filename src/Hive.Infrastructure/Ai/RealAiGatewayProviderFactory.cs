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

        var outputCapabilities = ResolveOutputCapabilities(options.OutputCapabilities);
        if (outputCapabilities.Error is { } capabilityError)
        {
            return RealAiGatewayProviderConfigurationResult.Failure(
                AiGatewayErrorCode.ConfigurationInvalid,
                capabilityError);
        }

        var pricingCatalog = ResolvePricingCatalog(options.Pricing);
        if (pricingCatalog.Error is { } pricingError)
        {
            return RealAiGatewayProviderConfigurationResult.Failure(
                AiGatewayErrorCode.ConfigurationInvalid,
                pricingError);
        }

        var settings = new RealAiGatewayProviderSettings(
            options.ApiKey,
            new AiProviderMetadata(providerId, modelId),
            parameters,
            endpoint,
            timeout,
            outputCapabilities.Capabilities!,
            pricingCatalog.Catalog);

        return RealAiGatewayProviderConfigurationResult.Success(settings);
    }

    private static string? Trimmed(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static OutputCapabilitiesResult ResolveOutputCapabilities(
        IEnumerable<string>? configuredModes)
    {
        if (configuredModes is null)
        {
            return OutputCapabilitiesResult.Success(
                new AiOutputProviderCapabilities([AiOutputConstraintMode.Text]));
        }

        var modes = new List<AiOutputConstraintMode>();
        foreach (var configuredMode in configuredModes)
        {
            if (!AiOutputConstraintModeContract.TryParseWireValue(
                configuredMode,
                out var mode))
            {
                return OutputCapabilitiesResult.Failure(
                    $"AI gateway real provider output capability '{configuredMode}' is invalid.");
            }

            if (!modes.Contains(mode))
            {
                modes.Add(mode);
            }
        }

        if (modes.Count == 0)
        {
            return OutputCapabilitiesResult.Failure(
                "AI gateway real provider requires at least one output capability.");
        }

        return OutputCapabilitiesResult.Success(new AiOutputProviderCapabilities(modes));
    }

    private sealed record OutputCapabilitiesResult(
        AiOutputProviderCapabilities? Capabilities,
        string? Error)
    {
        public static OutputCapabilitiesResult Success(
            AiOutputProviderCapabilities capabilities) =>
            new(capabilities, Error: null);

        public static OutputCapabilitiesResult Failure(string error) =>
            new(Capabilities: null, error);
    }

    private static PricingCatalogResult ResolvePricingCatalog(
        AiPricingCatalogOptions? options)
    {
        if (options is null)
        {
            return PricingCatalogResult.Success(catalog: null);
        }

        var version = Trimmed(options.Version);
        if (version is null || options.TokenUnit is not { } tokenUnit || tokenUnit <= 0)
        {
            return PricingCatalogResult.Failure(
                "AI gateway real provider pricing requires a version and positive 'TokenUnit'.");
        }

        if (options.Models is not { Length: > 0 })
        {
            return PricingCatalogResult.Failure(
                "AI gateway real provider pricing requires at least one model entry.");
        }

        var models = new List<AiModelPricing>(options.Models.Length);
        foreach (var configuredModel in options.Models)
        {
            if (configuredModel is null)
            {
                return PricingCatalogResult.Failure(
                    "AI gateway real provider pricing cannot contain null model entries.");
            }

            var providerId = Trimmed(configuredModel.ProviderId);
            var modelId = Trimmed(configuredModel.ModelId);
            var currency = Trimmed(configuredModel.Currency);
            if (providerId is null || modelId is null || !IsCurrency(currency))
            {
                return PricingCatalogResult.Failure(
                    "AI gateway real provider pricing model entries require provider, model and a three-letter uppercase currency.");
            }

            if (configuredModel.InputPrice is not { } inputPrice || inputPrice < 0 ||
                configuredModel.OutputPrice is not { } outputPrice || outputPrice < 0)
            {
                return PricingCatalogResult.Failure(
                    "AI gateway real provider pricing model entries require non-negative input and output prices.");
            }

            string[] aliases;
            try
            {
                aliases = (configuredModel.Aliases ?? [])
                    .Select(alias => Trimmed(alias) ?? throw new ArgumentException())
                    .ToArray();
                models.Add(new AiModelPricing(
                    providerId,
                    modelId,
                    aliases,
                    inputPrice,
                    outputPrice,
                    currency!));
            }
            catch (ArgumentException)
            {
                return PricingCatalogResult.Failure(
                    "AI gateway real provider pricing model aliases must be non-empty canonical text.");
            }
        }

        try
        {
            return PricingCatalogResult.Success(
                new AiPricingCatalog(version, tokenUnit, models));
        }
        catch (ArgumentException ex)
        {
            return PricingCatalogResult.Failure(
                $"AI gateway real provider pricing is invalid: {ex.Message}");
        }

        static bool IsCurrency(string? value) =>
            value is { Length: 3 } && value.All(character => character is >= 'A' and <= 'Z');
    }

    private sealed record PricingCatalogResult(
        AiPricingCatalog? Catalog,
        string? Error)
    {
        public static PricingCatalogResult Success(AiPricingCatalog? catalog) =>
            new(catalog, Error: null);

        public static PricingCatalogResult Failure(string error) =>
            new(Catalog: null, error);
    }
}
