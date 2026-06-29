using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed record NormalizedAiGatewayProviderResponse(
    AiGatewayResponse Response,
    RedactableAiGatewayProviderResponse RawResponse);
