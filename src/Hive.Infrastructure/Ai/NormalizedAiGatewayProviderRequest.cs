using Microsoft.Extensions.AI;

namespace Hive.Infrastructure.Ai;

internal sealed record NormalizedAiGatewayProviderRequest(
    IReadOnlyList<ChatMessage> Messages,
    ChatOptions Options);
