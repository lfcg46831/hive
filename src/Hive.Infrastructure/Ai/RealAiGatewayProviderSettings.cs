using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Validated, immutable settings for the optional real AI gateway provider.
/// Separates three concerns explicitly:
/// <list type="bullet">
/// <item><description><b>Secret</b>: <see cref="ApiKey"/> stays in infrastructure and is redacted in diagnostics.</description></item>
/// <item><description><b>Model parameters</b>: <see cref="DefaultParameters"/> as the HIVE contract <see cref="AiModelParameters"/>.</description></item>
/// <item><description><b>Position defaults</b>: <see cref="DefaultProvider"/>/<see cref="Endpoint"/>/<see cref="Timeout"/> applied when the position config omits a value.</description></item>
/// </list>
/// Implemented as a class (not a record) so the auto-generated <c>ToString</c>
/// cannot leak the secret.
/// </summary>
public sealed class RealAiGatewayProviderSettings
{
    public RealAiGatewayProviderSettings(
        string apiKey,
        AiProviderMetadata defaultProvider,
        AiModelParameters defaultParameters,
        Uri? endpoint = null,
        TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException(
                "API key cannot be empty or whitespace.",
                nameof(apiKey));
        }

        ArgumentNullException.ThrowIfNull(defaultProvider);
        ArgumentNullException.ThrowIfNull(defaultParameters);

        if (timeout is { } value && value <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Timeout must be greater than zero.");
        }

        ApiKey = apiKey;
        DefaultProvider = defaultProvider;
        DefaultParameters = defaultParameters;
        Endpoint = endpoint;
        Timeout = timeout;
    }

    /// <summary>Secret credential. Infrastructure-only; never logged or returned to the domain.</summary>
    public string ApiKey { get; }

    /// <summary>Provider/model defaults used when the position config omits them.</summary>
    public AiProviderMetadata DefaultProvider { get; }

    /// <summary>Model parameter defaults used when the position config omits them.</summary>
    public AiModelParameters DefaultParameters { get; }

    /// <summary>Optional absolute endpoint for the provider.</summary>
    public Uri? Endpoint { get; }

    /// <summary>Optional default request timeout.</summary>
    public TimeSpan? Timeout { get; }

    /// <summary>
    /// Resolves the effective, non-secret provider/model, parameters and timeout
    /// for a position. Position values override the configured defaults; absent
    /// position values fall back to the defaults without inventing new values.
    /// The secret stays in this settings instance and is never copied into the
    /// resolved model.
    /// </summary>
    public RealAiGatewayEffectiveModel Resolve(AiPositionRuntimeConfiguration? position)
    {
        if (position is null)
        {
            return new RealAiGatewayEffectiveModel(
                DefaultProvider,
                DefaultParameters,
                Timeout);
        }

        return new RealAiGatewayEffectiveModel(
            position.Primary,
            MergeParameters(DefaultParameters, position.Parameters),
            position.Timeout ?? Timeout);
    }

    public override string ToString() =>
        "RealAiGatewayProviderSettings { " +
        $"Provider = {DefaultProvider.ProviderId}/{DefaultProvider.ModelId}, " +
        $"Endpoint = {(Endpoint is null ? "(none)" : Endpoint.ToString())}, " +
        $"Timeout = {(Timeout?.ToString() ?? "(none)")}, " +
        "ApiKey = ***redacted*** }";

    private static AiModelParameters MergeParameters(
        AiModelParameters defaults,
        AiModelParameters position) =>
        new(
            position.Temperature ?? defaults.Temperature,
            position.MaxOutputTokens ?? defaults.MaxOutputTokens);
}
