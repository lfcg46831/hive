using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

public sealed record Escalation : OrgMessage
{
    public Escalation(
        MessageId id,
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to,
        ThreadId thread,
        Priority priority,
        int schemaVersion,
        DateTimeOffset sentAt,
        DateTimeOffset? deadline,
        string issue,
        string context,
        IEnumerable<string> optionsConsidered)
        : base(id, organizationId, from, to, thread, priority, schemaVersion, sentAt, deadline)
    {
        ArgumentNullException.ThrowIfNull(optionsConsidered);

        Issue = issue;
        Context = context;
        OptionsConsidered = optionsConsidered.ToImmutableArray();
    }

    public string Issue { get; }

    public string Context { get; }

    public ImmutableArray<string> OptionsConsidered { get; }

    public override MessageChannel Channel => MessageChannel.Vertical;
}
