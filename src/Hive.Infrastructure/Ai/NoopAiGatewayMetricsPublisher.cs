using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed class NoopAiGatewayMetricsPublisher : IAiGatewayMetricsPublisher
{
    public static NoopAiGatewayMetricsPublisher Instance { get; } = new();

    private NoopAiGatewayMetricsPublisher()
    {
    }

    public void Publish(AiGatewayAttemptMetrics metrics) =>
        ArgumentNullException.ThrowIfNull(metrics);

    public void Publish(AiGatewayFallbackSkipMetrics metrics) =>
        ArgumentNullException.ThrowIfNull(metrics);

    public void Publish(AiProviderCircuitTransitionMetrics metrics) =>
        ArgumentNullException.ThrowIfNull(metrics);
}
