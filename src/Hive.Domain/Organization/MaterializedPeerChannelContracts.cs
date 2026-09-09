using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Organization;

/// <summary>Thread-safe pure lookups over immutable per-organization snapshots.</summary>
public sealed class MaterializedPeerChannelContracts : IPeerChannelContracts
{
    private readonly ImmutableDictionary<OrganizationId, PeerChannelContractsSnapshot> _snapshots;

    public MaterializedPeerChannelContracts(IEnumerable<PeerChannelContractsSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var byOrganization = ImmutableDictionary.CreateBuilder<OrganizationId, PeerChannelContractsSnapshot>();
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (byOrganization.ContainsKey(snapshot.OrganizationId))
                throw new ArgumentException("More than one snapshot was supplied for the same organization.", nameof(snapshots));
            byOrganization.Add(snapshot.OrganizationId, snapshot);
        }

        _snapshots = byOrganization.ToImmutable();
    }

    public MaterializedPeerChannelContracts(PeerChannelContractsSnapshot snapshot)
        : this(new[] { snapshot ?? throw new ArgumentNullException(nameof(snapshot)) }) { }

    public ValueTask<PeerChannelConfiguration?> ResolveChannelAsync(
        OrganizationId organizationId,
        UnitId fromUnitId,
        UnitId toUnitId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(fromUnitId);
        ArgumentNullException.ThrowIfNull(toUnitId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshots.TryGetValue(organizationId, out var snapshot))
            throw PeerChannelContractNotFoundException.ForOrganization(organizationId);
        return new(snapshot.Resolve(fromUnitId, toUnitId));
    }
}
