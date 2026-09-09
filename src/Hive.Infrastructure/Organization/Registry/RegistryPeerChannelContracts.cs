using Hive.Domain.Identity;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.Registry;

/// <summary>Resolves against one current registry snapshot, including after GitOps updates/removals.</summary>
public sealed class RegistryPeerChannelContracts : IPeerChannelContracts
{
    private readonly IOrganizationRegistryReader _reader;

    public RegistryPeerChannelContracts(IOrganizationRegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(
        OrganizationId organizationId,
        UnitId fromUnitId,
        UnitId toUnitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(fromUnitId);
        ArgumentNullException.ThrowIfNull(toUnitId);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _reader.FindSnapshotAsync(organizationId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
            throw PeerChannelContractNotFoundException.ForOrganization(organizationId);
        if (snapshot.OrganizationId != organizationId)
            throw new InvalidOperationException("The registry returned a snapshot for a different organization.");
        return snapshot.PeerChannelContracts.Resolve(fromUnitId, toUnitId);
    }
}
