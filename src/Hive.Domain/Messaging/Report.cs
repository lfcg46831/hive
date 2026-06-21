using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record Report : OrgMessage
{
    public Report(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        DirectiveId aboutDirectiveId,
        ReportKind kind,
        string body)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(aboutDirectiveId);

        AboutDirectiveId = aboutDirectiveId;
        Kind = ReportKindContract.RequireDefined(kind, nameof(kind));
        Body = body;
    }

    public DirectiveId AboutDirectiveId { get; }

    public ReportKind Kind { get; }

    public string Body { get; }

    public override MessageChannel Channel => MessageChannel.Vertical;
}
