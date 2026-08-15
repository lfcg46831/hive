namespace Hive.Domain.Ai;

/// <summary>
/// Creates the provider-neutral, sanitized failures introduced by the AI gateway resilience
/// pipeline. Runtime queue, circuit and fallback state never crosses this boundary.
/// </summary>
public static class AiGatewayResilienceErrorCatalog
{
    private const string OverloadedMessage = "AI gateway is overloaded.";
    private const string CircuitOpenMessage = "AI provider circuit is open.";

    public static AiGatewayError GatewayOverloaded(
        AiGatewayRequest request,
        AiProviderMetadata? provider = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.GatewayOverloaded,
            OverloadedMessage,
            isRetryable: true,
            provider ?? request.Provider);
    }

    public static AiGatewayError CircuitOpen(
        AiGatewayRequest request,
        AiProviderMetadata? provider = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        return new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ProviderUnavailable,
            CircuitOpenMessage,
            isRetryable: false,
            provider ?? request.Provider,
            null,
            AiGatewayErrorReason.CircuitOpen);
    }

    public static AiGatewayError FallbackExhausted(AiGatewayError lastError)
    {
        ArgumentNullException.ThrowIfNull(lastError);

        return new AiGatewayError(
            lastError.OrganizationId,
            lastError.PositionId,
            lastError.ThreadId,
            lastError.MessageId,
            lastError.Code,
            lastError.Message,
            lastError.IsRetryable,
            lastError.Provider,
            lastError.Diagnostics,
            AiGatewayErrorReason.FallbackExhausted);
    }
}
