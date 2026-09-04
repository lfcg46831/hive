using System.Collections.Immutable;
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
        IEnumerable<AiGatewayAuditRedaction>? redactions = null,
        AiOutputConstraintMode? outputConstraintMode = null)
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
        OutputConstraintMode = outputConstraintMode is null
            ? null
            : AiOutputConstraintModeContract.RequireDefined(
                outputConstraintMode.Value,
                nameof(outputConstraintMode));
    }

    /// <summary>
    /// Additive shape of US-F1-05-T08: the same envelope plus the ordered journey of the
    /// attempts that produced it. The terminal reason of the journey is still
    /// <see cref="RejectionReason"/>, which carries the reason wire when the terminal
    /// error has one and the code wire when it has not.
    /// </summary>
    public AiGatewayAuditEnvelope(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt,
        AiGatewayCallResult result,
        AiGatewayAuditRequestSnapshot request,
        AiProviderMetadata? provider,
        AiGatewayAuditResponseSnapshot? response,
        AiGatewayAuditErrorSnapshot? error,
        AiTokenUsage? usage,
        AiCostMetadata? cost,
        string? rejectionReason,
        IEnumerable<AiGatewayAuditRedaction>? redactions,
        AiOutputConstraintMode? outputConstraintMode,
        IEnumerable<AiGatewayAuditAttemptSnapshot>? journey)
        : this(
            organizationId,
            positionId,
            threadId,
            messageId,
            startedAt,
            completedAt,
            result,
            request,
            provider,
            response,
            error,
            usage,
            cost,
            rejectionReason,
            redactions,
            outputConstraintMode)
    {
        Journey = AiContractGuards.Snapshot(journey, nameof(journey));
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

    /// <summary>
    /// Ordered, content-free record of every attempt of this call. Empty when the call
    /// never reached an attempt, as in a pre-call policy rejection.
    /// </summary>
    public IReadOnlyList<AiGatewayAuditAttemptSnapshot> Journey { get; } =
        ImmutableArray<AiGatewayAuditAttemptSnapshot>.Empty;

    public AiOutputConstraintMode? OutputConstraintMode { get; }
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
        AiProviderMetadata? provider = null,
        AiGatewayFailureDiagnostics? diagnostics = null)
    {
        Code = AiGatewayErrorCodeContract.RequireDefined(code, nameof(code));
        Message = AiContractGuards.RequireText(message, nameof(message));
        IsRetryable = isRetryable;
        Provider = provider;
        Diagnostics = diagnostics;
    }

    public AiGatewayAuditErrorSnapshot(
        AiGatewayErrorCode code,
        string message,
        bool isRetryable,
        AiProviderMetadata? provider,
        AiGatewayFailureDiagnostics? diagnostics,
        AiGatewayErrorReason reason)
        : this(code, message, isRetryable, provider, diagnostics)
    {
        Reason = AiGatewayErrorReasonContract.RequireDefined(reason, nameof(reason));
    }

    public AiGatewayErrorCode Code { get; }

    public string Message { get; }

    public bool IsRetryable { get; }

    public AiProviderMetadata? Provider { get; }

    public AiGatewayFailureDiagnostics? Diagnostics { get; }

    public AiGatewayErrorReason? Reason { get; }
}

/// <summary>
/// Provider-neutral, content-free record of one gateway attempt inside a journey
/// (US-F1-05-T08). Only identity, placement, effective provider/model, measurement and
/// the closed failure vocabulary travel here; prompt, response text, tool calls, free
/// metadata and transport diagnostics never do.
/// </summary>
public sealed record AiGatewayAuditAttemptSnapshot
{
    public AiGatewayAuditAttemptSnapshot(
        int candidateIndex,
        int attempt,
        bool reachedProvider,
        AiGatewayCallResult result,
        TimeSpan duration,
        string? providerId = null,
        string? modelId = null,
        TimeSpan? queueDuration = null,
        AiGatewayErrorCode? errorCode = null,
        AiGatewayErrorReason? errorReason = null)
    {
        if (candidateIndex < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(candidateIndex),
                candidateIndex,
                "AI gateway attempt candidate index cannot be negative.");
        }

        if (attempt <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(attempt),
                attempt,
                "AI gateway attempt number must be greater than zero.");
        }

        if (duration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(duration),
                duration,
                "AI gateway attempt duration cannot be negative.");
        }

        if (queueDuration is { } queued && queued < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueDuration),
                queueDuration,
                "AI gateway attempt queue duration cannot be negative.");
        }

        Result = AiGatewayCallResultContract.RequireDefined(result, nameof(result));

        if (Result == AiGatewayCallResult.Succeeded)
        {
            if (!reachedProvider)
            {
                throw new ArgumentException(
                    "Successful AI gateway attempt must have reached the provider.",
                    nameof(reachedProvider));
            }

            if (errorCode is not null || errorReason is not null)
            {
                throw new ArgumentException(
                    "Successful AI gateway attempt cannot carry an error code or reason.",
                    nameof(errorCode));
            }
        }
        else if (errorCode is null)
        {
            throw new ArgumentException(
                "Failed AI gateway attempt requires an error code.",
                nameof(errorCode));
        }

        CandidateIndex = candidateIndex;
        Attempt = attempt;
        AttemptId = AiGatewayCostAuditAttempt.FormatAttemptId(candidateIndex, attempt);
        ReachedProvider = reachedProvider;
        Duration = duration;
        ProviderId = AiContractGuards.OptionalText(providerId, nameof(providerId));
        ModelId = AiContractGuards.OptionalText(modelId, nameof(modelId));
        QueueDuration = queueDuration;
        ErrorCode = errorCode is null
            ? null
            : AiGatewayErrorCodeContract.RequireDefined(errorCode.Value, nameof(errorCode));
        ErrorReason = errorReason is null
            ? null
            : AiGatewayErrorReasonContract.RequireDefined(
                errorReason.Value,
                nameof(errorReason));
    }

    public string AttemptId { get; }

    public int CandidateIndex { get; }

    public int Attempt { get; }

    public bool ReachedProvider { get; }

    public AiGatewayCallResult Result { get; }

    public TimeSpan Duration { get; }

    public TimeSpan? QueueDuration { get; }

    public string? ProviderId { get; }

    public string? ModelId { get; }

    public AiGatewayErrorCode? ErrorCode { get; }

    public AiGatewayErrorReason? ErrorReason { get; }
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
