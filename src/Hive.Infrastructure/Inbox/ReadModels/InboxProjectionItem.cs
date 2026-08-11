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

public abstract record InboxProjectionMessageContent
{
    internal abstract InboxProjectionMessageType MessageType { get; }

    protected static string Text(string value, string parameterName) =>
        value ?? throw new ArgumentNullException(parameterName);
}

public sealed record InboxProjectionDirectiveContent : InboxProjectionMessageContent
{
    public InboxProjectionDirectiveContent(string objective, string context)
    {
        Objective = Text(objective, nameof(objective));
        Context = Text(context, nameof(context));
    }

    internal override InboxProjectionMessageType MessageType =>
        InboxProjectionMessageType.Directive;

    public string Objective { get; }

    public string Context { get; }
}

public sealed record InboxProjectionReportContent : InboxProjectionMessageContent
{
    public InboxProjectionReportContent(string body, ReportKind kind)
    {
        Body = Text(body, nameof(body));
        Kind = ReportKindContract.RequireDefined(kind, nameof(kind));
    }

    internal override InboxProjectionMessageType MessageType => InboxProjectionMessageType.Report;

    public string Body { get; }

    public ReportKind Kind { get; }
}

public sealed record InboxProjectionEscalationContent : InboxProjectionMessageContent
{
    public InboxProjectionEscalationContent(string issue, string context)
    {
        Issue = Text(issue, nameof(issue));
        Context = Text(context, nameof(context));
    }

    internal override InboxProjectionMessageType MessageType =>
        InboxProjectionMessageType.Escalation;

    public string Issue { get; }

    public string Context { get; }
}

public sealed record InboxProjectionMemoContent : InboxProjectionMessageContent
{
    public InboxProjectionMemoContent(string body)
    {
        Body = Text(body, nameof(body));
    }

    internal override InboxProjectionMessageType MessageType => InboxProjectionMessageType.Memo;

    public string Body { get; }
}

public sealed record InboxProjectionPeerRequestContent : InboxProjectionMessageContent
{
    public InboxProjectionPeerRequestContent(string ask)
    {
        Ask = Text(ask, nameof(ask));
    }

    internal override InboxProjectionMessageType MessageType =>
        InboxProjectionMessageType.PeerRequest;

    public string Ask { get; }
}

public sealed record InboxProjectionPeerResponseContent : InboxProjectionMessageContent
{
    public InboxProjectionPeerResponseContent(string body)
    {
        Body = Text(body, nameof(body));
    }

    internal override InboxProjectionMessageType MessageType =>
        InboxProjectionMessageType.PeerResponse;

    public string Body { get; }
}

public sealed record InboxProjectionApprovalRequestContent : InboxProjectionMessageContent
{
    public InboxProjectionApprovalRequestContent(string action, string justification)
    {
        Action = Text(action, nameof(action));
        Justification = Text(justification, nameof(justification));
    }

    internal override InboxProjectionMessageType MessageType =>
        InboxProjectionMessageType.ApprovalRequest;

    public string Action { get; }

    public string Justification { get; }
}

public sealed record InboxProjectionApprovalDecisionContent(string? Reason) :
    InboxProjectionMessageContent
{
    internal override InboxProjectionMessageType MessageType =>
        InboxProjectionMessageType.ApprovalDecision;
}

public sealed record InboxProjectionItem
{
    public InboxProjectionItem(
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
        InboxProjectionMessageContent Content,
        bool IsDelegated = false,
        DateTimeOffset? LastReminderAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(Origin);
        ArgumentNullException.ThrowIfNull(Destination);
        ArgumentNullException.ThrowIfNull(ThreadId);
        ArgumentNullException.ThrowIfNull(Content);
        if (Content.MessageType != Type)
        {
            throw new ArgumentException(
                "Projected message content must match the inbox item message type.",
                nameof(Content));
        }

        this.Key = Key;
        this.Type = Type;
        this.Origin = Origin;
        this.Destination = Destination;
        this.ThreadId = ThreadId;
        this.Priority = Priority;
        this.SentAtUtc = SentAtUtc;
        this.DeadlineAtUtc = DeadlineAtUtc;
        this.IsExpired = IsExpired;
        this.ResponseState = ResponseState;
        this.Approval = Approval;
        this.Content = Content;
        this.IsDelegated = IsDelegated;
        this.LastReminderAtUtc = LastReminderAtUtc;
    }

    public InboxProjectionItemKey Key { get; init; }

    public InboxProjectionMessageType Type { get; init; }

    public EndpointRef Origin { get; init; }

    public EndpointRef Destination { get; init; }

    public ThreadId ThreadId { get; init; }

    public Priority Priority { get; init; }

    public DateTimeOffset SentAtUtc { get; init; }

    public DateTimeOffset? DeadlineAtUtc { get; init; }

    public bool IsExpired { get; init; }

    public InboxProjectionResponseState ResponseState { get; init; }

    public InboxProjectionApproval? Approval { get; init; }

    public InboxProjectionMessageContent Content { get; init; }

    public bool IsDelegated { get; init; }

    public DateTimeOffset? LastReminderAtUtc { get; init; }
}

public sealed record InboxProjectionChange(
    InboxProjectionItem Item,
    string FactType,
    DateTimeOffset OccurredAtUtc);
