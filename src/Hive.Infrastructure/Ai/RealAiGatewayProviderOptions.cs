namespace Hive.Infrastructure.Ai;

/// <summary>
/// Raw configuration binding for the optional real AI gateway provider.
/// All values are nullable so the factory can detect absence and translate it
/// into a structured <see cref="Hive.Domain.Ai.AiGatewayErrorCode"/> instead of
/// throwing during binding. The secret <see cref="ApiKey"/> stays in
/// infrastructure and never crosses into <c>Hive.Domain</c>.
/// </summary>
public sealed class RealAiGatewayProviderOptions
{
    public const string SectionName = "Hive:AiGateway:Real";

    /// <summary>Default provider id applied when the position config omits it.</summary>
    public string? ProviderId { get; set; }

    /// <summary>Default model id applied when the position config omits it.</summary>
    public string? ModelId { get; set; }

    /// <summary>Optional absolute endpoint for the provider.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Secret credential. Infrastructure-only; never logged or returned to the domain.</summary>
    public string? ApiKey { get; set; }

    /// <summary>Default sampling temperature applied when the position config omits it.</summary>
    public decimal? Temperature { get; set; }

    /// <summary>Default maximum output tokens applied when the position config omits it.</summary>
    public int? MaxOutputTokens { get; set; }

    /// <summary>Optional default request timeout, in seconds.</summary>
    public int? TimeoutSeconds { get; set; }
}
