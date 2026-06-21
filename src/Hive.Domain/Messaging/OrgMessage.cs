using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public abstract record OrgMessage
{
    protected OrgMessage(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        ArgumentNullException.ThrowIfNull(thread);

        if (schemaVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(schemaVersion),
                schemaVersion,
                "Schema version must be greater than zero.");
        }

        Id = id;
        OrganizationId = organizationId;
        From = from;
        To = to;
        Thread = thread;
        Priority = PriorityContract.RequireDefined(priority, nameof(priority));
        SchemaVersion = schemaVersion;
        SentAt = sentAt;
        Deadline = deadline;
    }

    public MessageId Id { get; }

    public OrganizationId OrganizationId { get; }

    public EndpointRef From { get; }

    public EndpointRef To { get; }

    public ThreadId Thread { get; }

    public Priority Priority { get; }

    public int SchemaVersion { get; }

    public DateTimeOffset SentAt { get; }

    public DateTimeOffset? Deadline { get; }

    public abstract MessageChannel Channel { get; }
}
