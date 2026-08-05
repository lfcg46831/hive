using Hive.Domain.Identity;

namespace Hive.Infrastructure.Inbox.ReadModels;

public interface IInboxProjectionSnapshotReader
{
    bool IsAvailable { get; }

    ValueTask<InboxProjectionSnapshot> ReadAsync(
        OrganizationId organizationId,
        IReadOnlyCollection<PositionId> assignedPositionIds,
        CancellationToken cancellationToken = default);
}
