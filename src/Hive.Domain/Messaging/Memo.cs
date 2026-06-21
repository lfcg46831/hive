using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record Memo : OrgMessage
{
    public Memo(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string body)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        Body = body;
    }

    public string Body { get; }

    public override MessageChannel Channel => MessageChannel.Horizontal;
}
