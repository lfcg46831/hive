using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record Pulse : OrgMessage
{
    public Pulse(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string scheduleId,
        string payload)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ScheduleId = scheduleId;
        Payload = payload;
    }

    public string ScheduleId { get; }

    public string Payload { get; }

    public override MessageChannel Channel => MessageChannel.System;
}
