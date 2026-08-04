using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// Public metadata for one organizational message assigned to a human position.
/// </summary>
public sealed record InboxItem
{
    public InboxItem(
        string itemId,
        Guid messageId,
        string assignedPositionId,
        InboxMessageType type,
        InboxMessageEndpoint origin,
        InboxMessageEndpoint destination,
        Guid threadId,
        InboxPriority priority,
        DateTimeOffset sentAtUtc,
        DateTimeOffset? deadlineAtUtc,
        InboxReadState readState,
        InboxResponseState responseState,
        InboxApprovalMetadata? approval = null)
    {
        ItemId = InboxContractGuards.ItemIdentifier(itemId, nameof(itemId));
        MessageId = InboxContractGuards.MessageIdentifier(messageId, nameof(messageId));
        AssignedPositionId = InboxContractGuards.Identifier(
            assignedPositionId,
            nameof(assignedPositionId));
        Type = InboxContractGuards.DefinedEnum(type, nameof(type));
        Origin = origin ?? throw new ArgumentNullException(nameof(origin));
        Destination = destination ?? throw new ArgumentNullException(nameof(destination));
        ThreadId = InboxContractGuards.MessageIdentifier(threadId, nameof(threadId));
        Priority = InboxContractGuards.DefinedEnum(priority, nameof(priority));
        SentAtUtc = InboxContractGuards.UtcTimestamp(sentAtUtc, nameof(sentAtUtc));
        DeadlineAtUtc = InboxContractGuards.OptionalUtcTimestamp(
            deadlineAtUtc,
            nameof(deadlineAtUtc));
        ReadState = InboxContractGuards.DefinedEnum(readState, nameof(readState));
        ResponseState = InboxContractGuards.DefinedEnum(responseState, nameof(responseState));

        if (DeadlineAtUtc < SentAtUtc)
        {
            throw new ArgumentException(
                "Deadline cannot precede the message timestamp.",
                nameof(deadlineAtUtc));
        }

        var isApprovalMessage = Type is
            InboxMessageType.ApprovalRequest or InboxMessageType.ApprovalDecision;
        if (isApprovalMessage != (approval is not null))
        {
            throw new ArgumentException(
                "Approval metadata is required only for approval request and decision items.",
                nameof(approval));
        }

        if (Type == InboxMessageType.ApprovalRequest && approval!.RequestId != MessageId)
        {
            throw new ArgumentException(
                "Approval request metadata must reference the item message.",
                nameof(approval));
        }

        if (Type == InboxMessageType.ApprovalDecision)
        {
            if (approval!.State is not
                (InboxApprovalState.Approved or InboxApprovalState.Rejected))
            {
                throw new ArgumentException(
                    "An approval decision item must expose an approved or rejected state.",
                    nameof(approval));
            }

            if (approval.DecisionMessageId != MessageId)
            {
                throw new ArgumentException(
                    "Approval decision metadata must identify the item message as the decision.",
                    nameof(approval));
            }
        }

        Approval = approval;
    }

    [JsonPropertyName("item_id")]
    public string ItemId { get; }

    [JsonPropertyName("message_id")]
    public Guid MessageId { get; }

    [JsonPropertyName("assigned_position_id")]
    public string AssignedPositionId { get; }

    [JsonPropertyName("type")]
    public InboxMessageType Type { get; }

    [JsonPropertyName("origin")]
    public InboxMessageEndpoint Origin { get; }

    [JsonPropertyName("destination")]
    public InboxMessageEndpoint Destination { get; }

    [JsonPropertyName("thread_id")]
    public Guid ThreadId { get; }

    [JsonPropertyName("priority")]
    public InboxPriority Priority { get; }

    [JsonPropertyName("sent_at_utc")]
    public DateTimeOffset SentAtUtc { get; }

    [JsonPropertyName("deadline_at_utc")]
    public DateTimeOffset? DeadlineAtUtc { get; }

    [JsonPropertyName("read_state")]
    public InboxReadState ReadState { get; }

    [JsonPropertyName("response_state")]
    public InboxResponseState ResponseState { get; }

    [JsonPropertyName("approval")]
    public InboxApprovalMetadata? Approval { get; }
}
