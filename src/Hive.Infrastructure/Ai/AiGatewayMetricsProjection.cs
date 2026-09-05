using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal static class AiGatewayMetricsProjection
{
    public static AiGatewayAttemptMetrics? FromAuditEvent(
        AiGatewayCostAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);

        if (auditEvent.Scope != AiGatewayCostAuditScope.Attempt)
        {
            return null;
        }

        var candidateIndex = auditEvent.CandidateIndex ??
            throw new InvalidOperationException(
                "Attempt-scoped AI gateway audit event is missing its candidate index.");
        var attempt = auditEvent.Attempt ??
            throw new InvalidOperationException(
                "Attempt-scoped AI gateway audit event is missing its attempt number.");
        var reachedProvider = auditEvent.ReachedProvider ??
            throw new InvalidOperationException(
                "Attempt-scoped AI gateway audit event is missing provider reachability.");

        return new AiGatewayAttemptMetrics(
            auditEvent.Provider?.ProviderId,
            auditEvent.Provider?.ModelId,
            auditEvent.Result,
            auditEvent.ErrorCode,
            reachedProvider,
            auditEvent.QueueDuration,
            reachedProvider
                ? ProviderLatency(auditEvent.Duration, auditEvent.QueueDuration)
                : null,
            auditEvent.Cost,
            auditEvent.CostStatus,
            isRetry: attempt > 1,
            isFallback: candidateIndex > 0 && attempt == 1);
    }

    public static AiGatewayFallbackSkipMetrics FromFallbackSkip(
        AiGatewayFallbackSkip skip)
    {
        ArgumentNullException.ThrowIfNull(skip);

        return new AiGatewayFallbackSkipMetrics(
            skip.ProviderId,
            skip.ModelId,
            skip.Reason,
            skip.ErrorCode);
    }

    public static AiProviderCircuitTransitionMetrics FromCircuitTransition(
        AiProviderCircuitTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);

        return new AiProviderCircuitTransitionMetrics(
            transition.ProviderId,
            transition.PreviousState,
            transition.CurrentState,
            transition.Reason,
            transition.ErrorCode);
    }

    private static TimeSpan ProviderLatency(
        TimeSpan attemptDuration,
        TimeSpan? queueDuration)
    {
        var latency = attemptDuration - (queueDuration ?? TimeSpan.Zero);
        return latency < TimeSpan.Zero ? TimeSpan.Zero : latency;
    }
}
