using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record Directive : OrgMessage
{
    public Directive(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId,
        string objective,
        string context)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(directiveId);

        if (directiveId == parentDirectiveId)
        {
            throw new ArgumentException(
                "Parent directive cannot reference the directive itself.",
                nameof(parentDirectiveId));
        }

        DirectiveId = directiveId;
        ParentDirectiveId = parentDirectiveId;
        Objective = objective;
        Context = context;
    }

    public DirectiveId DirectiveId { get; }

    public DirectiveId? ParentDirectiveId { get; }

    public string Objective { get; }

    public string Context { get; }

    public override MessageChannel Channel => MessageChannel.Vertical;
}
