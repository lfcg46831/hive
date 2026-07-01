using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public sealed record AiGatewayAuditEnvelope
{
    public AiGatewayAuditEnvelope(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        AiGatewayCallResult result,
        AiGatewayAuditRequestSnapshot request,
        AiProviderMetadata? provider = null,
        AiGatewayAuditResponseSnapshot? response = null,
        AiGatewayAuditErrorSnapshot? error = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        string? rejectionReason = null,
        IEnumerable<AiGatewayAuditRedaction>? redactions = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(request);

        if (completedAt < startedAt)
        {
            throw new ArgumentOutOfRangeException(
                nameof(completedAt),
                completedAt,
                "AI gateway audit envelope completion cannot precede start.");
        }

        Result = AiGatewayCallResultContract.RequireDefined(result, nameof(result));
        var sanitizedRejectionReason = AiContractGuards.OptionalText(
            rejectionReason,
            nameof(rejectionReason));

        if (Result == AiGatewayCallResult.Succeeded &&
            (response is null || error is not null || sanitizedRejectionReason is not null))
        {
            throw new ArgumentException(
                "Successful AI gateway audit envelope requires a response and no error or rejection reason.",
                nameof(response));
        }

        if (Result == AiGatewayCallResult.Failed &&
            (error is null || response is not null || sanitizedRejectionReason is null))
        {
            throw new ArgumentException(
                "Failed AI gateway audit envelope requires an error, a rejection reason and no response.",
                nameof(error));
        }

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Request = request;
        Provider = provider;
        Response = response;
        Error = error;
        Usage = usage;
        Cost = cost;
        RejectionReason = sanitizedRejectionReason;
        Redactions = AiContractGuards.Snapshot(redactions, nameof(redactions));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    public DateTimeOffset StartedAt { get; }

    public DateTimeOffset CompletedAt { get; }

    public TimeSpan Duration => CompletedAt - StartedAt;

    public AiGatewayCallResult Result { get; }

    public AiGatewayAuditRequestSnapshot Request { get; }

    public AiProviderMetadata? Provider { get; }

    public AiGatewayAuditResponseSnapshot? Response { get; }

    public AiGatewayAuditErrorSnapshot? Error { get; }

    public AiTokenUsage? Usage { get; }

    public AiCostMetadata? Cost { get; }

    public string? RejectionReason { get; }

    public IReadOnlyList<AiGatewayAuditRedaction> Redactions { get; }
}

public sealed record AiGatewayAuditRequestSnapshot
{
    public AiGatewayAuditRequestSnapshot(
        string content,
        string? systemInstruction = null,
        IEnumerable<AiGatewayMessage>? contextMessages = null,
        IEnumerable<AiToolDefinition>? tools = null,
        AiModelParameters? modelParameters = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        AiProviderMetadata? provider = null,
        AiProcessingMode? processingMode = null,
        TimeSpan? timeout = null)
    {
        if (processingMode is { } mode)
        {
            AiProcessingModeContract.RequireDefined(mode, nameof(processingMode));
        }

        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "AI gateway audit request timeout must be greater than zero.");
        }

        Content = AiContractGuards.RequireText(content, nameof(content));
        SystemInstruction = AiContractGuards.OptionalText(
            systemInstruction,
            nameof(systemInstruction));
        ContextMessages = AiContractGuards.Snapshot(
            contextMessages,
            nameof(contextMessages));
        Tools = AiContractGuards.Snapshot(tools, nameof(tools));
        ModelParameters = modelParameters ?? AiModelParameters.Default;
        Metadata = AiContractGuards.SnapshotMetadata(metadata, nameof(metadata));
        Provider = provider;
        ProcessingMode = processingMode;
        Timeout = timeout;
    }

    public string Content { get; }

    public string? SystemInstruction { get; }

    public IReadOnlyList<AiGatewayMessage> ContextMessages { get; }

    public IReadOnlyList<AiToolDefinition> Tools { get; }

    public AiModelParameters ModelParameters { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public AiProviderMetadata? Provider { get; }

    public AiProcessingMode? ProcessingMode { get; }

    public TimeSpan? Timeout { get; }
}

public sealed record AiGatewayAuditResponseSnapshot
{
    public AiGatewayAuditResponseSnapshot(
        string? text,
        AiFinishReason finishReason,
        AiProviderMetadata? provider = null,
        IEnumerable<AiToolCall>? toolCalls = null)
    {
        var toolCallSnapshot = AiContractGuards.Snapshot(toolCalls, nameof(toolCalls));

        if (text is null && toolCallSnapshot.IsEmpty)
        {
            throw new ArgumentException(
                "AI gateway audit response requires text or at least one tool call.",
                nameof(text));
        }

        Text = AiContractGuards.OptionalText(text, nameof(text));
        FinishReason = AiFinishReasonContract.RequireDefined(
            finishReason,
            nameof(finishReason));
        Provider = provider;
        ToolCalls = toolCallSnapshot;
    }

    public string? Text { get; }

    public AiFinishReason FinishReason { get; }

    public AiProviderMetadata? Provider { get; }

    public IReadOnlyList<AiToolCall> ToolCalls { get; }
}

public sealed record AiGatewayAuditErrorSnapshot
{
    public AiGatewayAuditErrorSnapshot(
        AiGatewayErrorCode code,
        string message,
        bool isRetryable,
        AiProviderMetadata? provider = null)
    {
        Code = AiGatewayErrorCodeContract.RequireDefined(code, nameof(code));
        Message = AiContractGuards.RequireText(message, nameof(message));
        IsRetryable = isRetryable;
        Provider = provider;
    }

    public AiGatewayErrorCode Code { get; }

    public string Message { get; }

    public bool IsRetryable { get; }

    public AiProviderMetadata? Provider { get; }
}

public sealed record AiGatewayAuditRedaction
{
    public AiGatewayAuditRedaction(string path, string reason)
    {
        Path = AiContractGuards.RequireText(path, nameof(path));
        Reason = AiContractGuards.RequireText(reason, nameof(reason));
    }

    public string Path { get; }

    public string Reason { get; }
}
