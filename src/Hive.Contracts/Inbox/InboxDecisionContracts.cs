using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>Human approval input for one pending inbox approval request.</summary>
public sealed record InboxDecisionRequest
{
    public InboxDecisionRequest(bool? approved, string? reason = null)
    {
        Approved = approved;
        Reason = reason;
    }

    [JsonPropertyName("approved")]
    public bool? Approved { get; }

    [JsonPropertyName("reason")]
    public string? Reason { get; }
}

/// <summary>Metadata for the canonical approval decision emitted by the occupied position.</summary>
public sealed record InboxDecisionResponse
{
    public InboxDecisionResponse(
        Guid requestId,
        Guid messageId,
        bool approved,
        string? reason,
        string fromPositionId,
        string toPositionId,
        Guid threadId)
    {
        RequestId = InboxContractGuards.MessageIdentifier(requestId, nameof(requestId));
        MessageId = InboxContractGuards.MessageIdentifier(messageId, nameof(messageId));
        Approved = approved;
        Reason = reason is null
            ? null
            : InboxContractGuards.DisplayText(reason, nameof(reason));
        FromPositionId = InboxContractGuards.Identifier(
            fromPositionId,
            nameof(fromPositionId));
        ToPositionId = InboxContractGuards.Identifier(toPositionId, nameof(toPositionId));
        ThreadId = InboxContractGuards.MessageIdentifier(threadId, nameof(threadId));
    }

    [JsonPropertyName("request_id")]
    public Guid RequestId { get; }

    [JsonPropertyName("message_id")]
    public Guid MessageId { get; }

    [JsonPropertyName("approved")]
    public bool Approved { get; }

    [JsonPropertyName("reason")]
    public string? Reason { get; }

    [JsonPropertyName("from_position_id")]
    public string FromPositionId { get; }

    [JsonPropertyName("to_position_id")]
    public string ToPositionId { get; }

    [JsonPropertyName("thread_id")]
    public Guid ThreadId { get; }
}
