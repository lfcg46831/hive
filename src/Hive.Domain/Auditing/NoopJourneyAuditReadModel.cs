using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed class NoopJourneyAuditReadModel : IJourneyAuditReadModel
{
    public static readonly NoopJourneyAuditReadModel Instance = new();

    private NoopJourneyAuditReadModel()
    {
    }

    public JourneyAuditTimeline ReadTimeline(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId? directiveId = null) =>
        new(
            organizationId ?? throw new ArgumentNullException(nameof(organizationId)),
            threadId ?? throw new ArgumentNullException(nameof(threadId)),
            directiveId,
            []);
}
