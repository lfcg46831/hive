using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public interface IJourneyAuditReadModel
{
    JourneyAuditTimeline ReadTimeline(
        OrganizationId organizationId,
        ThreadId threadId,
        DirectiveId? directiveId = null);
}
