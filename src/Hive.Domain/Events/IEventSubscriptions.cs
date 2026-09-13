using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Read-only subscriptions from one materialized organization registry version.</summary>
/// <remarks>Technical failures propagate and must never be converted to empty subscriptions.</remarks>
public interface IEventSubscriptions
{
    /// <exception cref="EventSubscriptionsNotFoundException">The organization is unknown.</exception>
    /// <exception cref="OperationCanceledException">The cancellation token was cancelled.</exception>
    ValueTask<EventSubscriptionsSnapshot> GetSnapshotAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default);
}
