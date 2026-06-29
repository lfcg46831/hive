using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed class UnavailableAiGatewayProvider : IAiGatewayProvider
{
    public Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var error = new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ConfigurationInvalid,
            "AI gateway provider is not configured.",
            isRetryable: false);

        return Task.FromResult(AiGatewayResponse.Failed(error));
    }
}
