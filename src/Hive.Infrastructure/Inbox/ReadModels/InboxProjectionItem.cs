using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Infrastructure.Inbox.ReadModels;

public readonly record struct InboxProjectionItemKey(
    OrganizationId OrganizationId,
    PositionId AssignedPositionId,
    MessageId MessageId)
{
    public override string ToString() =>
        $"{OrganizationId.Value}/{AssignedPositionId.Value}/{MessageId}";
}

public enum InboxProjectionMessageType
{
    Directive,
    Report,
    Escalation,
    Memo,
    PeerRequest,
    PeerResponse,
    ApprovalRequest,
    ApprovalDecision,
}

public enum InboxProjectionResponseState
{
    NotApplicable,
    AwaitingResponse,
    Responded,
}

public enum InboxProjectionApprovalState
{
    Pending,
    Approved,
    Rejected,
    Expired,
}

public sealed record InboxProjectionApproval(
    MessageId RequestId,
    string Action,
    ApprovalPolicyRef Policy,
    InboxProjectionApprovalState State,
    MessageId? DecisionMessageId,
    DateTimeOffset? DecidedAtUtc);

public sealed record InboxProjectionItem(
    InboxProjectionItemKey Key,
    InboxProjectionMessageType Type,
    EndpointRef Origin,
    EndpointRef Destination,
    ThreadId ThreadId,
    Priority Priority,
    DateTimeOffset SentAtUtc,
    DateTimeOffset? DeadlineAtUtc,
    bool IsExpired,
    InboxProjectionResponseState ResponseState,
    InboxProjectionApproval? Approval,
    bool IsDelegated = false);

public sealed record InboxProjectionChange(
    InboxProjectionItem Item,
    string FactType,
    DateTimeOffset OccurredAtUtc);
