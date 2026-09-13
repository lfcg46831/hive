using Hive.Domain.Messaging;
using System.Globalization;

namespace Hive.Domain.Events;

/// <summary>Closed, mutually exclusive parameter shapes. ISO-8601 import belongs to T02.</summary>
public abstract record EventSubscriptionParameters
{
    private protected EventSubscriptionParameters() { }

    public abstract OrganizationEventType EventType { get; }
    internal abstract string IdentityValue { get; }
}

public sealed record DirectiveDeadlineParameters : EventSubscriptionParameters
{
    public DirectiveDeadlineParameters(TimeSpan within)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(within, TimeSpan.Zero);
        Within = within;
    }

    public TimeSpan Within { get; }
    public override OrganizationEventType EventType => OrganizationEventType.DirectiveDeadlineApproaching;
    internal override string IdentityValue => Within.Ticks.ToString(CultureInfo.InvariantCulture);
}

public sealed record PositionBlockedParameters : EventSubscriptionParameters
{
    public PositionBlockedParameters(TimeSpan after)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(after, TimeSpan.Zero);
        After = after;
    }

    public TimeSpan After { get; }
    public override OrganizationEventType EventType => OrganizationEventType.PositionBlockedProlonged;
    internal override string IdentityValue => After.Ticks.ToString(CultureInfo.InvariantCulture);
}

public sealed record BudgetThresholdParameters : EventSubscriptionParameters
{
    public BudgetThresholdParameters(int thresholdPercent)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(thresholdPercent, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(thresholdPercent, 100);
        ThresholdPercent = thresholdPercent;
    }

    public int ThresholdPercent { get; }
    public override OrganizationEventType EventType => OrganizationEventType.BudgetThresholdReached;
    internal override string IdentityValue => ThresholdPercent.ToString(CultureInfo.InvariantCulture);
}

/// <summary>A declaration within a subscribing position; parameters identify duplicates in T02.</summary>
public sealed record EventSubscription
{
    public EventSubscription(EventSubscriptionParameters parameters, bool isCritical = false, Priority priority = Priority.Normal)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        PriorityContract.RequireDefined(priority, nameof(priority));
        Parameters = parameters;
        IsCritical = isCritical;
        Priority = priority;
    }

    public EventSubscriptionParameters Parameters { get; }
    public OrganizationEventType EventType => Parameters.EventType;
    public bool IsCritical { get; }
    public Priority Priority { get; }
}
