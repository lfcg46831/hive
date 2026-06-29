using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

public sealed class AiGateway : IAiGateway
{
    private readonly IAiGatewayProvider _provider;

    public AiGateway(IAiGatewayProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _provider.CompleteAsync(request, cancellationToken);
    }
}
