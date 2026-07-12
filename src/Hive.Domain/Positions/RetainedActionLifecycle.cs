using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

public sealed record AuthorizeRetainedAction : PositionCommand
{
    public AuthorizeRetainedAction(AuthorizationGrant grant) =>
        Grant = grant ?? throw new ArgumentNullException(nameof(grant));

    public AuthorizationGrant Grant { get; }
}

public sealed record DenyRetainedAction : PositionCommand
{
    public DenyRetainedAction(AuthorizationDenial denial) =>
        Denial = denial ?? throw new ArgumentNullException(nameof(denial));

    public AuthorizationDenial Denial { get; }
}

public sealed record ConsumeRetainedAction : PositionCommand
{
    public ConsumeRetainedAction(RetainedActionId actionId, MessageId grantId)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
    }

    public RetainedActionId ActionId { get; }
    public MessageId GrantId { get; }
}

public sealed record ExpireRetainedAction : PositionCommand
{
    public ExpireRetainedAction(RetainedActionId actionId, MessageId grantId, string reEscalationCode)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        ReEscalationCode = CommandText.RequireContent(reEscalationCode, nameof(reEscalationCode));
    }

    public RetainedActionId ActionId { get; }
    public MessageId GrantId { get; }
    public string ReEscalationCode { get; }
}

public sealed record ReturnRetainedAction : PositionCommand
{
    public ReturnRetainedAction(RetainedActionId actionId, MessageId grantId, string reEscalationCode)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        ReEscalationCode = CommandText.RequireContent(reEscalationCode, nameof(reEscalationCode));
    }

    public RetainedActionId ActionId { get; }
    public MessageId GrantId { get; }
    public string ReEscalationCode { get; }
}

public sealed record ResumeRetainedAction : PositionCommand
{
    public ResumeRetainedAction(RetainedActionId actionId, Guid attemptId)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        if (attemptId == Guid.Empty)
        {
            throw new ArgumentException("Resume attempt id cannot be empty.", nameof(attemptId));
        }

        AttemptId = attemptId;
    }

    public RetainedActionId ActionId { get; }
    public Guid AttemptId { get; }
}

public sealed record RetainedActionAuthorized : PositionEvent
{
    public RetainedActionAuthorized(AuthorizationGrant grant, DateTimeOffset occurredAt) : base(occurredAt) =>
        Grant = grant ?? throw new ArgumentNullException(nameof(grant));

    public AuthorizationGrant Grant { get; }
}

public sealed record RetainedActionDenied : PositionEvent
{
    public RetainedActionDenied(AuthorizationDenial denial, DateTimeOffset occurredAt) : base(occurredAt) =>
        Denial = denial ?? throw new ArgumentNullException(nameof(denial));

    public AuthorizationDenial Denial { get; }
}

public sealed record RetainedActionConsumed : PositionEvent
{
    public RetainedActionConsumed(RetainedActionId actionId, MessageId grantId, DateTimeOffset occurredAt)
        : base(occurredAt)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
    }

    public RetainedActionId ActionId { get; }
    public MessageId GrantId { get; }
}

public sealed record RetainedActionExpired : PositionEvent
{
    public RetainedActionExpired(
        RetainedActionId actionId,
        MessageId grantId,
        string reEscalationCode,
        DateTimeOffset occurredAt) : base(occurredAt)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        ReEscalationCode = CommandText.RequireContent(reEscalationCode, nameof(reEscalationCode));
    }

    public RetainedActionId ActionId { get; }
    public MessageId GrantId { get; }
    public string ReEscalationCode { get; }
}

public sealed record RetainedActionReturned : PositionEvent
{
    public RetainedActionReturned(
        RetainedActionId actionId,
        MessageId grantId,
        string reEscalationCode,
        DateTimeOffset occurredAt) : base(occurredAt)
    {
        ActionId = actionId ?? throw new ArgumentNullException(nameof(actionId));
        GrantId = grantId ?? throw new ArgumentNullException(nameof(grantId));
        ReEscalationCode = CommandText.RequireContent(reEscalationCode, nameof(reEscalationCode));
    }

    public RetainedActionId ActionId { get; }
    public MessageId GrantId { get; }
    public string ReEscalationCode { get; }
}
