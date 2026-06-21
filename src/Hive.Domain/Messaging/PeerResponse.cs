using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record PeerResponse : OrgMessage
{
    public PeerResponse(
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
        string body)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(inReplyTo);

        InReplyTo = inReplyTo;
        Body = body;
    }

    public MessageId InReplyTo { get; }

    public string Body { get; }

    public override MessageChannel Channel => MessageChannel.Horizontal;
}
