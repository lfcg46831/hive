using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

public interface IOrganogramSnapshotReader
{
    bool IsAvailable { get; }

    ValueTask<OrganogramSnapshot?> FindAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
