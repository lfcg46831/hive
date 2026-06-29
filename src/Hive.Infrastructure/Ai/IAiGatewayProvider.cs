using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

public interface IAiGatewayProvider
{
    Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken);
}
