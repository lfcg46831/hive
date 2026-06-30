using Hive.Domain.Identity;

namespace Hive.Domain.Ai;

public sealed record AiGatewayRequest
{
    public AiGatewayRequest(
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId messageId,
        string content,
        string? systemInstruction = null,
        IEnumerable<AiGatewayMessage>? contextMessages = null,
        IEnumerable<AiToolDefinition>? tools = null,
        AiModelParameters? modelParameters = null,
        IReadOnlyDictionary<string, string>? metadata = null,
        AiProviderMetadata? provider = null,
        AiProcessingMode? processingMode = null,
        TimeSpan? timeout = null,
        AiGatewayPolicy? policy = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(threadId);
        ArgumentNullException.ThrowIfNull(messageId);
        if (processingMode is { } mode)
        {
            AiProcessingModeContract.RequireDefined(mode, nameof(processingMode));
        }

        if (timeout is { } timeoutValue && timeoutValue <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "AI gateway request timeout must be greater than zero.");
        }

        OrganizationId = organizationId;
        PositionId = positionId;
        ThreadId = threadId;
        MessageId = messageId;
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
        Policy = policy;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId MessageId { get; }

    public string Content { get; }

    public string? SystemInstruction { get; }

    public IReadOnlyList<AiGatewayMessage> ContextMessages { get; }

    public IReadOnlyList<AiToolDefinition> Tools { get; }

    public AiModelParameters ModelParameters { get; }

    public IReadOnlyDictionary<string, string> Metadata { get; }

    public AiProviderMetadata? Provider { get; }

    public AiProcessingMode? ProcessingMode { get; }

    public TimeSpan? Timeout { get; }

    public AiGatewayPolicy? Policy { get; }
}
