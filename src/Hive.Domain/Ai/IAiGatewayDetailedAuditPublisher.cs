namespace Hive.Domain.Ai;

public interface IAiGatewayDetailedAuditPublisher
{
    void Publish(AiGatewayAuditEnvelope envelope);
}
