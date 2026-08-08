using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>Plain-text human input for one correlated inbox response.</summary>
public sealed record InboxReplyRequest
{
    public InboxReplyRequest(string? body, string? reportKind = null)
    {
        Body = body;
        ReportKind = reportKind;
    }

    [JsonPropertyName("body")]
    public string? Body { get; }

    /// <summary>
    /// Required as <c>progress</c> or <c>done</c> when replying to a Directive; omitted for the
    /// other supported mappings.
    /// </summary>
    [JsonPropertyName("report_kind")]
    public string? ReportKind { get; }
}

/// <summary>Metadata for the canonical organizational message emitted by the occupied position.</summary>
public sealed record InboxReplyResponse
{
    public InboxReplyResponse(
        Guid sourceMessageId,
        Guid messageId,
        InboxMessageType type,
        string fromPositionId,
        string toPositionId,
        Guid threadId,
        Guid? directiveId = null)
    {
        SourceMessageId = InboxContractGuards.MessageIdentifier(
            sourceMessageId,
            nameof(sourceMessageId));
        MessageId = InboxContractGuards.MessageIdentifier(messageId, nameof(messageId));
        Type = InboxContractGuards.DefinedEnum(type, nameof(type));
        FromPositionId = InboxContractGuards.Identifier(
            fromPositionId,
            nameof(fromPositionId));
        ToPositionId = InboxContractGuards.Identifier(toPositionId, nameof(toPositionId));
        ThreadId = InboxContractGuards.MessageIdentifier(threadId, nameof(threadId));
        DirectiveId = InboxContractGuards.OptionalMessageIdentifier(
            directiveId,
            nameof(directiveId));
    }

    [JsonPropertyName("source_message_id")]
    public Guid SourceMessageId { get; }

    [JsonPropertyName("message_id")]
    public Guid MessageId { get; }

    [JsonPropertyName("type")]
    public InboxMessageType Type { get; }

    [JsonPropertyName("from_position_id")]
    public string FromPositionId { get; }

    [JsonPropertyName("to_position_id")]
    public string ToPositionId { get; }

    [JsonPropertyName("thread_id")]
    public Guid ThreadId { get; }

    [JsonPropertyName("directive_id")]
    public Guid? DirectiveId { get; }
}
