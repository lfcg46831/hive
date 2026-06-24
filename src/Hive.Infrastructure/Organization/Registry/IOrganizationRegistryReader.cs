using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

public interface IOrganizationRegistryReader
{
    ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
