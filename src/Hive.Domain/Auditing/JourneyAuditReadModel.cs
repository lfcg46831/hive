using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed class JourneyAuditReadModel : IJourneyAuditReadModel
{
    private readonly IJourneyAuditLog _auditLog;

    public JourneyAuditReadModel(IJourneyAuditLog auditLog)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    public JourneyAuditTimeline ReadTimeline(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId? directiveId = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(threadId);

        var entries = _auditLog
            .ReadByThread(threadId, directiveId)
            .Where(record => record.OrganizationId == organizationId)
            .Select(JourneyAuditTimelineEntry.FromRecord)
            .ToArray();

        return new JourneyAuditTimeline(
            organizationId,
            threadId,
            directiveId,
            entries);
    }
}
