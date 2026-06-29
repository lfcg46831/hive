namespace Hive.Domain.Ai;

public interface IAiGateway
{
    Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default);
}
