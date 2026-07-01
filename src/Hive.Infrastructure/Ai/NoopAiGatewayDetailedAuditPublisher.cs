using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed class NoopAiGatewayDetailedAuditPublisher : IAiGatewayDetailedAuditPublisher
{
    public static readonly NoopAiGatewayDetailedAuditPublisher Instance = new();

    private NoopAiGatewayDetailedAuditPublisher()
    {
    }

    public void Publish(AiGatewayAuditEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
    }
}
