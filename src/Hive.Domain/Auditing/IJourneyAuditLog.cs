using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public interface IJourneyAuditLog
{
    void Append(JourneyAuditRecord record);

    IReadOnlyList<JourneyAuditRecord> ReadByThread(
        ThreadId threadId,
        DirectiveId? directiveId = null);
}
