using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public sealed record AiGatewayError
{
    public AiGatewayError(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        AiGatewayErrorCode code,
        string message,
        bool isRetryable,
        AiProviderMetadata? provider = null,
        AiGatewayFailureDiagnostics? diagnostics = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        Code = AiGatewayErrorCodeContract.RequireDefined(code, nameof(code));
        Message = AiContractGuards.RequireText(message, nameof(message));
        IsRetryable = isRetryable;
        Provider = provider;
        Diagnostics = diagnostics;
    }

    public AiGatewayError(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        AiGatewayErrorCode code,
        string message,
        bool isRetryable,
        AiProviderMetadata? provider,
        AiGatewayFailureDiagnostics? diagnostics,
        AiGatewayErrorReason reason)
        : this(
            organizationId,
            positionId,
            threadId,
            messageId,
            code,
            message,
            isRetryable,
            provider,
            diagnostics)
    {
        Reason = AiGatewayErrorReasonContract.RequireDefined(reason, nameof(reason));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    public AiGatewayErrorCode Code { get; }

    public string Message { get; }

    public bool IsRetryable { get; }

    public AiProviderMetadata? Provider { get; }

    public AiGatewayFailureDiagnostics? Diagnostics { get; }

    public AiGatewayErrorReason? Reason { get; }
}
