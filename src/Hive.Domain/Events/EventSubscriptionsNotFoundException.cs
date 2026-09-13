using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Unknown organization, distinct from a known organization with no subscribers.</summary>
public sealed class EventSubscriptionsNotFoundException : Exception
{
    private EventSubscriptionsNotFoundException(string message) : base(message) { }

    public static EventSubscriptionsNotFoundException ForOrganization(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        return new($"Organization '{organizationId.Value}' was not found in the event subscriptions registry.");
    }
}
