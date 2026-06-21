using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record ApprovalRequest : OrgMessage
{
    public ApprovalRequest(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string action,
        string justification,
        ApprovalPolicyRef policy)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(policy);

        Action = action;
        Justification = justification;
        Policy = policy;
    }

    public string Action { get; }

    public string Justification { get; }

    public ApprovalPolicyRef Policy { get; }

    public override MessageChannel Channel => MessageChannel.Governance;
}
