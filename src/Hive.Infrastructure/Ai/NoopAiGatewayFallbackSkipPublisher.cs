using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed class NoopAiGatewayFallbackSkipPublisher : IAiGatewayFallbackSkipPublisher
{
    public static NoopAiGatewayFallbackSkipPublisher Instance { get; } = new();

    private NoopAiGatewayFallbackSkipPublisher()
    {
    }

    public void Publish(AiGatewayFallbackSkip skip)
    {
        ArgumentNullException.ThrowIfNull(skip);
    }
}
