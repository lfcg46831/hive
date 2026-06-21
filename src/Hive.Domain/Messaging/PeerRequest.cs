using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record PeerRequest : OrgMessage
{
    public PeerRequest(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string ask)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        Ask = ask;
    }

    public string Ask { get; }

    public override MessageChannel Channel => MessageChannel.Horizontal;
}
