using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record EventTrigger : OrgMessage
{
    public EventTrigger(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string eventType,
        string payload)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        EventType = eventType;
        Payload = payload;
    }

    public string EventType { get; }

    public string Payload { get; }

    public override MessageChannel Channel => MessageChannel.System;
}
