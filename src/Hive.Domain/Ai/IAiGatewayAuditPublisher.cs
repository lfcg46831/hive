namespace Hive.Domain.Ai;

public interface IAiGatewayAuditPublisher
{
    void Publish(AiGatewayCostAuditEvent @event);
}
