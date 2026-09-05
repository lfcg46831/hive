namespace Hive.Domain.Ai;

/// <summary>
/// Executes one policy-validated attempt at the provider's admission/circuit owner.
/// The caller owns retries, fallback and all attempt/journey auditing.
/// </summary>
public interface IAiGatewayAttemptExecutor
{
    Task<AiGatewayAttemptResult> ExecuteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record AiGatewayAttemptResult(
    AiGatewayResponse Response,
    TimeSpan? QueueDuration,
    bool ReachedProvider);
