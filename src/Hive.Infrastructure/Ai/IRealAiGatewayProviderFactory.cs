namespace Hive.Infrastructure.Ai;

/// <summary>
/// Factory for the optional real AI gateway provider configuration. It binds and
/// validates <see cref="RealAiGatewayProviderOptions"/> into immutable
/// <see cref="RealAiGatewayProviderSettings"/> or a structured failure. It does
/// not call <c>Microsoft.Extensions.AI</c>, open network connections, instantiate
/// the real adapter (US-F0-07-T05b) or decide default activation (US-F0-07-T05c).
/// </summary>
public interface IRealAiGatewayProviderFactory
{
    RealAiGatewayProviderConfigurationResult ResolveSettings();
}
