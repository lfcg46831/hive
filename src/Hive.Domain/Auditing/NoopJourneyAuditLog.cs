using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed class NoopJourneyAuditLog : IJourneyAuditLog
{
    public static readonly NoopJourneyAuditLog Instance = new();

    private NoopJourneyAuditLog()
    {
    }

    public void Append(JourneyAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
    }

    public IReadOnlyList<JourneyAuditRecord> ReadByThread(
        ThreadId threadId,
        DirectiveId? directiveId = null)
    {
        ArgumentNullException.ThrowIfNull(threadId);
        return [];
    }
}
