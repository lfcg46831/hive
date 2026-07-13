using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public sealed record AiGatewayCostAuditEvent
{
    private const string DirectiveIdMetadataKey = "directive_id";

    public AiGatewayCostAuditEvent(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        AiGatewayCallResult result,
        AiProviderMetadata? provider = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        AiGatewayErrorCode? errorCode = null,
        bool? isRetryable = null,
        DirectiveId? directiveId = null,
        AiOutputConstraintMode? outputConstraintMode = null,
        AiAppliedPricing? appliedPricing = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);

        if (completedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                completedAt,
                "AI gateway audit event completion cannot precede start.");
        }

        Result = AiGatewayCallResultContract.RequireDefined(result, nameof(result));

        if (Result == AiGatewayCallResult.Succeeded &&
            (errorCode is not null || isRetryable is not null))
        {
            throw new ArgumentException(
                "Successful AI gateway audit event cannot carry error payload.",
                nameof(errorCode));
        }

        if (Result == AiGatewayCallResult.Failed &&
            (errorCode is null || isRetryable is null))
        {
            throw new ArgumentException(
                "Failed AI gateway audit event requires error code and retryability.",
                nameof(errorCode));
        }

        if (appliedPricing is not null &&
            (cost is null ||
             !cost.IsEstimated ||
             !string.Equals(cost.Currency, appliedPricing.Currency, StringComparison.Ordinal)))
        {
            throw new ArgumentException(
                "Applied pricing requires estimated cost metadata in the same currency.",
                nameof(appliedPricing));
        }

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Provider = provider;
        Usage = usage;
        Cost = cost;
        AppliedPricing = appliedPricing;
        CostStatus = cost is null
            ? AiCostStatus.Unavailable
            : appliedPricing is not null
                ? AiCostStatus.Estimated
                : AiCostStatus.ProviderReported;
        DirectiveId = directiveId;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
        IsRetryable = isRetryable;
        OutputConstraintMode = outputConstraintMode is null
            ? null
            : AiOutputConstraintModeContract.RequireDefined(
                outputConstraintMode.Value,
                nameof(outputConstraintMode));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    public DirectiveId? DirectiveId { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public TimeSpan Duration => CompletedAt - StartedAt;

    public AiGatewayCallResult Result { get; }

    public AiProviderMetadata? Provider { get; }

    public AiTokenUsage? Usage { get; }

    public AiCostMetadata? Cost { get; }

    public AiAppliedPricing? AppliedPricing { get; }

    public AiCostStatus CostStatus { get; }

    public AiGatewayErrorCode? ErrorCode { get; }

    public bool? IsRetryable { get; }

    public AiOutputConstraintMode? OutputConstraintMode { get; }

    public static AiGatewayCostAuditEvent FromResponse(
        AiGatewayRequest request,
        AiGatewayResponse response,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        if (response.IsSuccess)
        {
            return new AiGatewayCostAuditEvent(
                response.OrganizationId,
                response.PositionId,
                response.ThreadId,
                response.MessageId,
                startedAt,
                completedAt,
                AiGatewayCallResult.Succeeded,
                response.Provider ?? request.Provider,
                response.Usage,
                response.Cost,
                directiveId: DirectiveIdFrom(request),
                outputConstraintMode: response.OutputConstraintMode,
                appliedPricing: response.AppliedPricing);
        }

        var error = response.Error!;
        return new AiGatewayCostAuditEvent(
            error.OrganizationId,
            error.PositionId,
            error.ThreadId,
            error.MessageId,
            startedAt,
            completedAt,
            AiGatewayCallResult.Failed,
            error.Provider ?? request.Provider,
            errorCode: error.Code,
            isRetryable: error.IsRetryable,
            directiveId: DirectiveIdFrom(request),
            outputConstraintMode: response.OutputConstraintMode);
    }

    private static DirectiveId? DirectiveIdFrom(AiGatewayRequest request)
    {
        if (!request.Metadata.TryGetValue(DirectiveIdMetadataKey, out var value))
        {
            return null;
        }

        return Guid.TryParse(value, out var parsed)
            ? DirectiveId.From(parsed)
            : null;
    }
}
