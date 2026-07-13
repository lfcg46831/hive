using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public sealed record AiGatewayResponse
{
    private AiGatewayResponse(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        string? text,
        AiFinishReason? finishReason,
        AiProviderMetadata? provider,
        IEnumerable<AiToolCall>? toolCalls,
        AiTokenUsage? usage,
        AiCostMetadata? cost,
        AiGatewayError? error,
        AiOutputConstraintMode? outputConstraintMode,
        AiAppliedPricing? appliedPricing)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);

        var isSuccess = error is null;
        var toolCallSnapshot = AiContractGuards.Snapshot(toolCalls, nameof(toolCalls));

        if (isSuccess && finishReason is null)
        {
            throw new ArgumentException(
                "Successful AI gateway response requires a finish reason.",
                nameof(finishReason));
        }

        if (isSuccess && text is null && toolCallSnapshot.IsEmpty)
        {
            throw new ArgumentException(
                "Successful AI gateway response requires text or at least one tool call.",
                nameof(text));
        }

        if (!isSuccess && (
            text is not null ||
            finishReason is not null ||
            provider is not null ||
            usage is not null ||
            cost is not null ||
            toolCalls is not null ||
            appliedPricing is not null))
        {
            throw new ArgumentException(
                "Failed AI gateway response cannot carry success payload.",
                nameof(error));
        }

        ValidateAppliedPricing(cost, appliedPricing);

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        Text = text;
        FinishReason = finishReason is null
            ? null
            : AiFinishReasonContract.RequireDefined(finishReason.Value, nameof(finishReason));
        Provider = provider;
        ToolCalls = toolCallSnapshot;
        Usage = usage;
        Cost = cost;
        Error = error;
        AppliedPricing = appliedPricing;
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

    public bool IsSuccess => Error is null;

    public bool IsFailure => Error is not null;

    public string? Text { get; }

    public AiFinishReason? FinishReason { get; }

    public AiProviderMetadata? Provider { get; }

    public IReadOnlyList<AiToolCall> ToolCalls { get; }

    public AiTokenUsage? Usage { get; }

    public AiCostMetadata? Cost { get; }

    public AiAppliedPricing? AppliedPricing { get; }

    public AiGatewayError? Error { get; }

    public AiOutputConstraintMode? OutputConstraintMode { get; }

    public static AiGatewayResponse Succeeded(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        string? text,
        AiFinishReason finishReason,
        AiProviderMetadata? provider = null,
        IEnumerable<AiToolCall>? toolCalls = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        AiOutputConstraintMode? outputConstraintMode = null,
        AiAppliedPricing? appliedPricing = null) =>
        new(
            organizationId,
            positionId,
            threadId,
            messageId,
            AiContractGuards.OptionalText(text, nameof(text)),
            finishReason,
            provider,
            toolCalls,
            usage,
            cost,
            error: null,
            outputConstraintMode: outputConstraintMode,
            appliedPricing: appliedPricing);

    public static AiGatewayResponse Failed(
        AiGatewayError error,
        AiOutputConstraintMode? outputConstraintMode = null)
    {
        ArgumentNullException.ThrowIfNull(error);

        return new(
            error.OrganizationId,
            error.PositionId,
            error.ThreadId,
            error.MessageId,
            text: null,
            finishReason: null,
            provider: null,
            toolCalls: null,
            usage: null,
            cost: null,
            error,
            outputConstraintMode: outputConstraintMode,
            appliedPricing: null);
    }

    private static void ValidateAppliedPricing(
        AiCostMetadata? cost,
        AiAppliedPricing? appliedPricing)
    {
        if (appliedPricing is null)
        {
            return;
        }

        if (cost is null || !cost.IsEstimated)
        {
            throw new ArgumentException(
                "Applied pricing requires estimated cost metadata.",
                nameof(appliedPricing));
        }

        if (!string.Equals(
            cost.Currency,
            appliedPricing.Currency,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Applied pricing currency must match cost currency.",
                nameof(appliedPricing));
        }
    }
}
