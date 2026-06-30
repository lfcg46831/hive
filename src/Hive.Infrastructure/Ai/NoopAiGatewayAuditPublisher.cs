using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed class NoopAiGatewayAuditPublisher : IAiGatewayAuditPublisher
{
    public static readonly NoopAiGatewayAuditPublisher Instance = new();

    private NoopAiGatewayAuditPublisher()
    {
    }

    public void Publish(AiGatewayCostAuditEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);
    }
}
