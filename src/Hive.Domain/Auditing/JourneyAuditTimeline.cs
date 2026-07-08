using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed record JourneyAuditTimeline
{
    public JourneyAuditTimeline(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId? directiveId,
        IReadOnlyList<JourneyAuditTimelineEntry> entries)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        DirectiveId = directiveId;
        Entries = Snapshot(entries);
    }

    public OrganizationId OrganizationId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId? DirectiveId { get; }

    public IReadOnlyList<JourneyAuditTimelineEntry> Entries { get; }

    private static IReadOnlyList<JourneyAuditTimelineEntry> Snapshot(
        IReadOnlyList<JourneyAuditTimelineEntry> entries)
    {
        ArgumentNullException.ThrowIfNull(entries);

        var snapshot = entries.ToArray();
        if (snapshot.Any(entry => entry is null))
        {
            throw new ArgumentException(
                "Journey timeline entries cannot contain null entries.",
                nameof(entries));
        }

        return snapshot;
    }
}
