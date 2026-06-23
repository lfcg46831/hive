namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// One event subscription of an occupant (§6.2 <c>occupant.subscriptions[]</c>): the
/// <see cref="Event"/> the occupant reacts to and the <see cref="Within"/> reaction window
/// expressed as an ISO-8601 duration. The window string is stored verbatim; parsing it is deferred.
/// </summary>
public sealed record SubscriptionConfiguration
{
    /// <summary>Creates a subscription to <paramref name="event"/> with reaction window <paramref name="within"/>.</summary>
    public SubscriptionConfiguration(string @event, string within)
    {
        ArgumentNullException.ThrowIfNull(@event);
        ArgumentNullException.ThrowIfNull(within);

        Event = @event;
        Within = within;
    }

    /// <summary>The event the occupant reacts to.</summary>
    public string Event { get; }

    /// <summary>The reaction window as an ISO-8601 duration (for example <c>PT4H</c>).</summary>
    public string Within { get; }
}
