using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Thread-safe pure lookups over immutable per-organization snapshots.</summary>
public sealed class MaterializedEventSubscriptions : IEventSubscriptions
{
    private readonly ImmutableDictionary<OrganizationId, EventSubscriptionsSnapshot> _snapshots;

    public MaterializedEventSubscriptions(IEnumerable<EventSubscriptionsSnapshot> snapshots)
    {
        ArgumentNullException.ThrowIfNull(snapshots);
        var byOrganization = ImmutableDictionary.CreateBuilder<OrganizationId, EventSubscriptionsSnapshot>();
        foreach (var snapshot in snapshots)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            if (byOrganization.ContainsKey(snapshot.OrganizationId))
                throw new ArgumentException("More than one snapshot was supplied for the same organization.", nameof(snapshots));
            byOrganization.Add(snapshot.OrganizationId, snapshot);
        }
        _snapshots = byOrganization.ToImmutable();
    }

    public MaterializedEventSubscriptions(EventSubscriptionsSnapshot snapshot)
        : this(new[] { snapshot ?? throw new ArgumentNullException(nameof(snapshot)) }) { }

    public ValueTask<EventSubscriptionsSnapshot> GetSnapshotAsync(OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshots.TryGetValue(organizationId, out var snapshot))
            throw EventSubscriptionsNotFoundException.ForOrganization(organizationId);
        return new(snapshot);
    }
}
