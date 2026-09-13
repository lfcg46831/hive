using Hive.Domain.Events;
using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.Registry;

/// <summary>Reads one current registry snapshot per query, without retaining stale declarations.</summary>
public sealed class RegistryEventSubscriptions : IEventSubscriptions
{
    private readonly IOrganizationRegistryReader _reader;

    public RegistryEventSubscriptions(IOrganizationRegistryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
    }

    public async ValueTask<EventSubscriptionsSnapshot> GetSnapshotAsync(OrganizationId organizationId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = await _reader.FindSnapshotAsync(organizationId, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (snapshot is null)
            throw EventSubscriptionsNotFoundException.ForOrganization(organizationId);
        if (snapshot.OrganizationId != organizationId)
            throw new InvalidOperationException("The registry returned a snapshot for a different organization.");
        return snapshot.EventSubscriptions;
    }
}
