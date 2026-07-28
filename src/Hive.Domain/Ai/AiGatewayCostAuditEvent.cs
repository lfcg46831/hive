using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public sealed record AiGatewayCostAuditEvent
{
    private const string DirectiveIdMetadataKey = "directive_id";
    private const string OperationMetadataKey = "hive.operation";
    private const string IterationMetadataKey = "iteration";

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
        AiAppliedPricing? appliedPricing = null,
        string? operation = null,
        int? iteration = null,
        AiFinishReason? finishReason = null,
        int? providerStatusCode = null,
        TimeSpan? requestTimeout = null,
        int? maxOutputTokens = null)
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

        if (iteration is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(iteration),
                iteration,
                "AI gateway audit iteration must be greater than zero.");
        }

        if (finishReason is { } reason)
        {
            AiFinishReasonContract.RequireDefined(reason, nameof(finishReason));
        }

        if (providerStatusCode is < 100 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(providerStatusCode),
                providerStatusCode,
                "Provider status code must be between 100 and 599.");
        }

        if (Result == AiGatewayCallResult.Succeeded && providerStatusCode is not null)
        {
            throw new ArgumentException(
                "Successful AI gateway audit event cannot carry a provider failure status code.",
                nameof(providerStatusCode));
        }

        if (requestTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requestTimeout),
                requestTimeout,
                "AI gateway audit request timeout must be greater than zero.");
        }

        if (maxOutputTokens is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxOutputTokens),
                maxOutputTokens,
                "AI gateway audit max output tokens must be greater than zero.");
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
        Operation = operation is null
            ? null
            : AiContractGuards.RequireText(operation, nameof(operation));
        Iteration = iteration;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
        IsRetryable = isRetryable;
        FinishReason = finishReason;
        ProviderStatusCode = providerStatusCode;
        RequestTimeout = requestTimeout;
        MaxOutputTokens = maxOutputTokens;
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

    public string? Operation { get; }

    public int? Iteration { get; }

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

    public AiFinishReason? FinishReason { get; }

    public int? ProviderStatusCode { get; }

    public TimeSpan? RequestTimeout { get; }

    public int? MaxOutputTokens { get; }

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
                appliedPricing: response.AppliedPricing,
                operation: OperationFrom(request),
                iteration: IterationFrom(request),
                finishReason: response.FinishReason,
                requestTimeout: request.Timeout,
                maxOutputTokens: request.ModelParameters.MaxOutputTokens);
        }

        var error = response.Error!;
        var diagnostics = error.Diagnostics;
        return new AiGatewayCostAuditEvent(
            error.OrganizationId,
            error.PositionId,
            error.ThreadId,
            error.MessageId,
            startedAt,
            completedAt,
            AiGatewayCallResult.Failed,
            error.Provider ?? request.Provider,
            diagnostics?.Usage,
            diagnostics?.Cost,
            errorCode: error.Code,
            isRetryable: error.IsRetryable,
            directiveId: DirectiveIdFrom(request),
            outputConstraintMode: response.OutputConstraintMode,
            appliedPricing: diagnostics?.AppliedPricing,
            operation: OperationFrom(request),
            iteration: IterationFrom(request),
            finishReason: diagnostics?.FinishReason,
            providerStatusCode: diagnostics?.ProviderStatusCode,
            requestTimeout: request.Timeout,
            maxOutputTokens: request.ModelParameters.MaxOutputTokens);
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

    private static string? OperationFrom(AiGatewayRequest request) =>
        request.Metadata.TryGetValue(OperationMetadataKey, out var value) &&
        !string.IsNullOrWhiteSpace(value)
            ? value
            : null;

    private static int? IterationFrom(AiGatewayRequest request) =>
        request.Metadata.TryGetValue(IterationMetadataKey, out var value) &&
        int.TryParse(
            value,
            System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture,
            out var parsed) &&
        parsed > 0
            ? parsed
            : null;
}
