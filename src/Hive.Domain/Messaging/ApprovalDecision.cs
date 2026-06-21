using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record ApprovalDecision : OrgMessage
{
    public ApprovalDecision(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        MessageId requestId,
        bool approved,
        string? reason)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(requestId);

        RequestId = requestId;
        Approved = approved;
        Reason = reason;
    }

    public MessageId RequestId { get; }

    public bool Approved { get; }

    public string? Reason { get; }

    public override MessageChannel Channel => MessageChannel.Governance;
}
