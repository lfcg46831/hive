using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public enum AiGatewayFallbackSkipReason
{
    DuplicateCandidate = 1,
    PolicyRevalidationFailed = 2,
}

public static class AiGatewayFallbackSkipReasonContract
{
    private static readonly AiProtocolEnumWireContract<AiGatewayFallbackSkipReason> Contract =
        new(
            (AiGatewayFallbackSkipReason.DuplicateCandidate, "duplicate-candidate"),
            (AiGatewayFallbackSkipReason.PolicyRevalidationFailed, "policy-revalidation-failed"));

    public static AiGatewayFallbackSkipReason RequireDefined(
        AiGatewayFallbackSkipReason value,
        string parameterName) =>
        Contract.RequireDefined(value, parameterName);

    public static string ToWireValue(AiGatewayFallbackSkipReason value) =>
        Contract.ToWireValue(value);

    public static AiGatewayFallbackSkipReason ParseWireValue(string value) =>
        Contract.ParseWireValue(value);

    public static bool TryParseWireValue(
        string? value,
        out AiGatewayFallbackSkipReason reason) =>
        Contract.TryParseWireValue(value, out reason);
}

/// <summary>
/// Provider-neutral, content-free observation of a declared fallback candidate that was
/// never executed. The candidate index is one-based because index zero is the primary
/// request already fixed by the pre-call policy and is never skipped.
/// </summary>
public sealed record AiGatewayFallbackSkip
{
    public AiGatewayFallbackSkip(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        int candidateIndex,
        string providerId,
        string modelId,
        DateTimeOffset occurredAt,
        AiGatewayFallbackSkipReason reason,
        AiGatewayErrorCode? errorCode = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);

        if (candidateIndex <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateIndex),
                candidateIndex,
                "AI gateway fallback candidate index must be greater than zero.");
        }

        if (occurredAt == default)
        {
            throw new ArgumentException(
                "AI gateway fallback skip timestamp must be specified.",
                nameof(occurredAt));
        }

        reason = AiGatewayFallbackSkipReasonContract.RequireDefined(reason, nameof(reason));
        ValidateErrorCode(reason, errorCode);

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        CandidateIndex = candidateIndex;
        ProviderId = AiContractGuards.RequireText(providerId, nameof(providerId));
        ModelId = AiContractGuards.RequireText(modelId, nameof(modelId));
        OccurredAt = occurredAt;
        Reason = reason;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    /// <summary>The one-based position of the skipped candidate in the declared chain.</summary>
    public int CandidateIndex { get; }

    public string ProviderId { get; }

    public string ModelId { get; }

    public DateTimeOffset OccurredAt { get; }

    public AiGatewayFallbackSkipReason Reason { get; }

    public AiGatewayErrorCode? ErrorCode { get; }

    private static void ValidateErrorCode(
        AiGatewayFallbackSkipReason reason,
        AiGatewayErrorCode? errorCode)
    {
        var requiresErrorCode =
            reason is AiGatewayFallbackSkipReason.PolicyRevalidationFailed;

        if (requiresErrorCode == (errorCode is null))
        {
            throw new ArgumentException(
                requiresErrorCode
                    ? "AI gateway fallback policy revalidation skip requires an error code."
                    : "AI gateway fallback duplicate candidate skip cannot carry an error code.",
                nameof(errorCode));
        }
    }
}

public interface IAiGatewayFallbackSkipPublisher
{
    void Publish(AiGatewayFallbackSkip skip);
}
