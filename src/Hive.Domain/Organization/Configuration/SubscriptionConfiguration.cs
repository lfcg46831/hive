using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml;
using Hive.Domain.Events;
using Hive.Domain.Messaging;

namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// A validated F1 subscription with mutually exclusive parameters and canonical durations.
/// </summary>
public sealed record SubscriptionConfiguration
{
    /// <summary>Creates a subscription to <paramref name="event"/> with reaction window <paramref name="within"/>.</summary>
    public SubscriptionConfiguration(string @event, string within)
        : this(@event, within, null, null, false, "normal")
    {
    }

    [JsonConstructor]
    public SubscriptionConfiguration(
        string @event,
        string? within = null,
        string? after = null,
        int? thresholdPercent = null,
        bool isCritical = false,
        string priority = "normal")
    {
        var type = OrganizationEventTypeContract.ParseWireValue(@event);
        PriorityContract.ParseWireValue(priority);
        if (type != OrganizationEventType.DirectiveDeadlineApproaching && within is not null
            || type != OrganizationEventType.PositionBlockedProlonged && after is not null
            || type != OrganizationEventType.BudgetThresholdReached && thresholdPercent is not null)
        {
            throw new ArgumentException("Subscription parameters must belong to the declared event type.");
        }

        switch (type)
        {
            case OrganizationEventType.DirectiveDeadlineApproaching:
                Within = NormalizeDuration(within, nameof(within));
                break;
            case OrganizationEventType.PositionBlockedProlonged:
                After = NormalizeDuration(after, nameof(after));
                break;
            case OrganizationEventType.BudgetThresholdReached:
                ArgumentNullException.ThrowIfNull(thresholdPercent);
                ThresholdPercent = new BudgetThresholdParameters(thresholdPercent.Value).ThresholdPercent;
                break;
        }
        Event = @event;
        IsCritical = isCritical;
        Priority = priority;
    }

    /// <summary>The event the occupant reacts to.</summary>
    public string Event { get; }

    /// <summary>The reaction window as an ISO-8601 duration (for example <c>PT4H</c>).</summary>
    public string? Within { get; }

    public string? After { get; }
    public int? ThresholdPercent { get; }
    public bool IsCritical { get; }
    public string Priority { get; }

    /// <summary>The normalized identity within a position, excluding delivery policy.</summary>
    public string GetIdentity() =>
        $"{Event}:{Within ?? After ?? ThresholdPercent!.Value.ToString(CultureInfo.InvariantCulture)}";

    public EventSubscription ToEventSubscription() => new(
        OrganizationEventTypeContract.ParseWireValue(Event) switch
        {
            OrganizationEventType.DirectiveDeadlineApproaching => new DirectiveDeadlineParameters(XmlConvert.ToTimeSpan(Within!)),
            OrganizationEventType.PositionBlockedProlonged => new PositionBlockedParameters(XmlConvert.ToTimeSpan(After!)),
            OrganizationEventType.BudgetThresholdReached => new BudgetThresholdParameters(ThresholdPercent!.Value),
            _ => throw new InvalidOperationException("Validated event type is not mapped."),
        },
        IsCritical,
        PriorityContract.ParseWireValue(Priority));

    private static string NormalizeDuration(string? value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        TimeSpan duration;
        try
        {
            duration = XmlConvert.ToTimeSpan(value);
        }
        catch (Exception exception) when (exception is FormatException or OverflowException)
        {
            throw new ArgumentException("Expected a positive ISO-8601 duration.", parameterName, exception);
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(duration, TimeSpan.Zero, parameterName);
        return XmlConvert.ToString(duration);
    }
}
