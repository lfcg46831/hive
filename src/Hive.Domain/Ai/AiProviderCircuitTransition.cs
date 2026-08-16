using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public enum AiProviderCircuitState
{
    Closed = 1,
    Open = 2,
    HalfOpen = 3,
}

public static class AiProviderCircuitStateContract
{
    private static readonly AiProtocolEnumWireContract<AiProviderCircuitState> Contract = new(
        (AiProviderCircuitState.Closed, "closed"),
        (AiProviderCircuitState.Open, "open"),
        (AiProviderCircuitState.HalfOpen, "half-open"));

    public static AiProviderCircuitState RequireDefined(
        AiProviderCircuitState value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiProviderCircuitState value) =>
        Contract.ToWireValue(value);

    public static AiProviderCircuitState ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiProviderCircuitState state) =>
        Contract.TryParseWireValue(value, out state);
}

public enum AiProviderCircuitTransitionReason
{
    FailureThresholdReached = 1,
    OpenDurationElapsed = 2,
    HalfOpenProbeSucceeded = 3,
    HalfOpenProbeFailed = 4,
}

public static class AiProviderCircuitTransitionReasonContract
{
    private static readonly AiProtocolEnumWireContract<
        AiProviderCircuitTransitionReason> Contract = new(
            (AiProviderCircuitTransitionReason.FailureThresholdReached,
                "failure-threshold-reached"),
            (AiProviderCircuitTransitionReason.OpenDurationElapsed,
                "open-duration-elapsed"),
            (AiProviderCircuitTransitionReason.HalfOpenProbeSucceeded,
                "half-open-probe-succeeded"),
            (AiProviderCircuitTransitionReason.HalfOpenProbeFailed,
                "half-open-probe-failed"));

    public static AiProviderCircuitTransitionReason RequireDefined(
        AiProviderCircuitTransitionReason value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiProviderCircuitTransitionReason value) =>
        Contract.ToWireValue(value);

    public static AiProviderCircuitTransitionReason ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiProviderCircuitTransitionReason reason) =>
        Contract.TryParseWireValue(value, out reason);
}

/// <summary>
/// Provider-neutral, content-free observation of an AI provider circuit state change.
/// The provider id is absent only for the legacy local bucket used by requests that
/// have no effective provider metadata.
/// </summary>
public sealed record AiProviderCircuitTransition
{
    public AiProviderCircuitTransition(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        string? providerId,
        AiProviderCircuitState previousState,
        AiProviderCircuitState currentState,
        DateTimeOffset occurredAt,
        AiProviderCircuitTransitionReason reason,
        AiGatewayErrorCode? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);

        if (occurredAt == default)
        {
            throw new ArgumentException(
                "AI provider circuit transition timestamp must be specified.",
                nameof(occurredAt));
        }

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

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        ProviderId = providerId is null
            ? null
            : AiContractGuards.RequireText(providerId, nameof(providerId));
        PreviousState = previousState;
        CurrentState = currentState;
        OccurredAt = occurredAt;
        Reason = reason;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(
                errorCode.Value,
                nameof(errorCode));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    public string? ProviderId { get; }

    public AiProviderCircuitState PreviousState { get; }

    public AiProviderCircuitState CurrentState { get; }

    public DateTimeOffset OccurredAt { get; }

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
                "AI provider circuit transition states do not match its reason.",
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
                    ? "AI provider circuit failure transition requires an error code."
                    : "AI provider circuit non-failure transition cannot carry an error code.",
                nameof(errorCode));
        }
    }
}

public interface IAiProviderCircuitTransitionPublisher
{
    void Publish(AiProviderCircuitTransition transition);
}
