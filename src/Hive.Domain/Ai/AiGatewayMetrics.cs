namespace Hive.Domain.Ai;

/// <summary>
/// Low-cardinality, provider-neutral metrics derived from one attempt-scoped gateway
/// cost/audit event. One instance represents one attempt result; retry and fallback
/// flags let an observability adapter increment their counters without inspecting
/// journey correlation (US-F1-05-T09).
/// </summary>
public sealed record AiGatewayAttemptMetrics
{
    public AiGatewayAttemptMetrics(
        string? providerId,
        string? modelId,
        AiGatewayCallResult result,
        AiGatewayErrorCode? errorCode,
        bool reachedProvider,
        TimeSpan? queueLatency,
        TimeSpan? providerLatency,
        AiCostMetadata? cost,
        AiCostStatus costStatus,
        bool isRetry,
        bool isFallback)
    {
        ValidateProvider(providerId, modelId);
        result = AiGatewayCallResultContract.RequireDefined(result, nameof(result));

        if (result == AiGatewayCallResult.Succeeded && errorCode is not null)
        {
            throw new ArgumentException(
                "Successful AI gateway attempt metrics cannot carry an error code.",
                nameof(errorCode));
        }

        if (result == AiGatewayCallResult.Failed && errorCode is null)
        {
            throw new ArgumentException(
                "Failed AI gateway attempt metrics require an error code.",
                nameof(errorCode));
        }

        if (queueLatency is { } queued && queued < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueLatency),
                queueLatency,
                "AI gateway queue latency cannot be negative.");
        }

        if (providerLatency is { } providerDuration && providerDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerLatency),
                providerLatency,
                "AI gateway provider latency cannot be negative.");
        }

        if (reachedProvider != (providerLatency is not null))
        {
            throw new ArgumentException(
                reachedProvider
                    ? "An attempt that reached the provider requires provider latency."
                    : "An attempt that did not reach the provider cannot carry provider latency.",
                nameof(providerLatency));
        }

        costStatus = AiCostStatusContract.RequireDefined(costStatus, nameof(costStatus));
        if ((cost is null) != (costStatus == AiCostStatus.Unavailable))
        {
            throw new ArgumentException(
                cost is null
                    ? "Unavailable AI gateway cost metrics cannot carry an available cost status."
                    : "Available AI gateway cost metrics cannot use the unavailable cost status.",
                nameof(costStatus));
        }

        ProviderId = providerId is null
            ? null
            : AiContractGuards.RequireText(providerId, nameof(providerId));
        ModelId = modelId is null
            ? null
            : AiContractGuards.RequireText(modelId, nameof(modelId));
        Result = result;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
        ReachedProvider = reachedProvider;
        QueueLatency = queueLatency;
        ProviderLatency = providerLatency;
        Cost = cost;
        CostStatus = costStatus;
        IsRetry = isRetry;
        IsFallback = isFallback;
    }

    public string? ProviderId { get; }

    public string? ModelId { get; }

    public AiGatewayCallResult Result { get; }

    public AiGatewayErrorCode? ErrorCode { get; }

    public bool ReachedProvider { get; }

    public TimeSpan? QueueLatency { get; }

    public TimeSpan? ProviderLatency { get; }

    public AiCostMetadata? Cost { get; }

    public AiCostStatus CostStatus { get; }

    public bool IsRetry { get; }

    public bool IsFallback { get; }

    private static void ValidateProvider(string? providerId, string? modelId)
    {
        if ((providerId is null) != (modelId is null))
        {
            throw new ArgumentException(
                "AI gateway attempt metrics require provider and model together.",
                providerId is null ? nameof(providerId) : nameof(modelId));
        }
    }
}

/// <summary>
/// One fallback advancement whose declared candidate was skipped before execution.
/// Executed fallback candidates are represented by <see cref="AiGatewayAttemptMetrics.IsFallback"/>.
/// </summary>
public sealed record AiGatewayFallbackSkipMetrics
{
    public AiGatewayFallbackSkipMetrics(
        string providerId,
        string modelId,
        AiGatewayFallbackSkipReason reason,
        AiGatewayErrorCode? errorCode = null)
    {
        reason = AiGatewayFallbackSkipReasonContract.RequireDefined(reason, nameof(reason));
        var requiresErrorCode =
            reason == AiGatewayFallbackSkipReason.PolicyRevalidationFailed;

        if (requiresErrorCode == (errorCode is null))
        {
            throw new ArgumentException(
                requiresErrorCode
                    ? "Fallback policy revalidation metrics require an error code."
                    : "Duplicate fallback metrics cannot carry an error code.",
                nameof(errorCode));
        }

        ProviderId = AiContractGuards.RequireText(providerId, nameof(providerId));
        ModelId = AiContractGuards.RequireText(modelId, nameof(modelId));
        Reason = reason;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
    }

    public string ProviderId { get; }

    public string ModelId { get; }

    public AiGatewayFallbackSkipReason Reason { get; }

    public AiGatewayErrorCode? ErrorCode { get; }
}

/// <summary>
/// One provider circuit transition reduced to bounded metric dimensions. Provider id is
/// absent only for the legacy local bucket (US-F1-05-T09).
/// </summary>
public sealed record AiProviderCircuitTransitionMetrics
{
    public AiProviderCircuitTransitionMetrics(
        string? providerId,
        AiProviderCircuitState previousState,
        AiProviderCircuitState currentState,
        AiProviderCircuitTransitionReason reason,
        AiGatewayErrorCode? errorCode = null)
    {
        previousState = AiProviderCircuitStateContract.RequireDefined(
            previousState,
            nameof(previousState));
        currentState = AiProviderCircuitStateContract.RequireDefined(
            currentState,
            nameof(currentState));
        reason = AiProviderCircuitTransitionReasonContract.RequireDefined(
            reason,
            nameof(reason));

        ValidateTransition(previousState, currentState, reason);
        ValidateErrorCode(reason, errorCode);

        ProviderId = providerId is null
            ? null
            : AiContractGuards.RequireText(providerId, nameof(providerId));
        PreviousState = previousState;
        CurrentState = currentState;
        Reason = reason;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
    }

    public string? ProviderId { get; }

    public AiProviderCircuitState PreviousState { get; }

    public AiProviderCircuitState CurrentState { get; }

    public AiProviderCircuitTransitionReason Reason { get; }

    public AiGatewayErrorCode? ErrorCode { get; }

    private static void ValidateTransition(
        AiProviderCircuitState previousState,
        AiProviderCircuitState currentState,
        AiProviderCircuitTransitionReason reason)
    {
        var isValid = reason switch
        {
            AiProviderCircuitTransitionReason.FailureThresholdReached =>
                previousState == AiProviderCircuitState.Closed &&
                currentState == AiProviderCircuitState.Open,
            AiProviderCircuitTransitionReason.OpenDurationElapsed =>
                previousState == AiProviderCircuitState.Open &&
                currentState == AiProviderCircuitState.HalfOpen,
            AiProviderCircuitTransitionReason.HalfOpenProbeSucceeded =>
                previousState == AiProviderCircuitState.HalfOpen &&
                currentState == AiProviderCircuitState.Closed,
            AiProviderCircuitTransitionReason.HalfOpenProbeFailed =>
                previousState == AiProviderCircuitState.HalfOpen &&
                currentState == AiProviderCircuitState.Open,
            _ => false,
        };

        if (!isValid)
        {
            throw new ArgumentException(
                "AI provider circuit metric states do not match its reason.",
                nameof(reason));
        }
    }

    private static void ValidateErrorCode(
        AiProviderCircuitTransitionReason reason,
        AiGatewayErrorCode? errorCode)
    {
        var isFailureTransition = reason is
            AiProviderCircuitTransitionReason.FailureThresholdReached or
            AiProviderCircuitTransitionReason.HalfOpenProbeFailed;

        if (isFailureTransition == (errorCode is null))
        {
            throw new ArgumentException(
                isFailureTransition
                    ? "AI provider circuit failure metrics require an error code."
                    : "AI provider circuit non-failure metrics cannot carry an error code.",
                nameof(errorCode));
        }
    }
}

public interface IAiGatewayMetricsPublisher
{
    void Publish(AiGatewayAttemptMetrics metrics);

    void Publish(AiGatewayFallbackSkipMetrics metrics);

    void Publish(AiProviderCircuitTransitionMetrics metrics);
}
