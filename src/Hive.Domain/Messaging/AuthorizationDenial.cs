using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record AuthorizationDenial : OrgMessage
{
    public AuthorizationDenial(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        MessageId inReplyTo,
        RetainedActionId retainedActionId,
        string reason)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(inReplyTo);
        ArgumentNullException.ThrowIfNull(retainedActionId);

        InReplyTo = inReplyTo;
        RetainedActionId = retainedActionId;
        Reason = reason;
    }

    public MessageId InReplyTo { get; }

    public RetainedActionId RetainedActionId { get; }

    public string Reason { get; }

    public override MessageChannel Channel => MessageChannel.Governance;
}
