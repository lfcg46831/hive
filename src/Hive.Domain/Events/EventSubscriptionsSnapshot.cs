using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>One subscribing position and its immutable typed declaration.</summary>
public sealed record EventSubscriber
{
    public EventSubscriber(PositionId positionId, EventSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentNullException.ThrowIfNull(subscription);
        PositionId = positionId;
        Subscription = subscription;
    }

    public PositionId PositionId { get; }
    public EventSubscription Subscription { get; }
}

/// <summary>Immutable, canonically ordered subscriptions within one organization registry version.</summary>
public sealed class EventSubscriptionsSnapshot
{
    private readonly ImmutableDictionary<OrganizationEventType, ImmutableArray<EventSubscriber>> _subscribers;

    private EventSubscriptionsSnapshot(OrganizationId organizationId,
        ImmutableDictionary<OrganizationEventType, ImmutableArray<EventSubscriber>> subscribers)
    {
        OrganizationId = organizationId;
        _subscribers = subscribers;
    }

    public OrganizationId OrganizationId { get; }

    public static Builder CreateBuilder(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        return new(organizationId);
    }

    /// <summary>Returns subscribers by ordinal position id, then numeric parameter; empty means confirmed absence.</summary>
    public ImmutableArray<EventSubscriber> Resolve(OrganizationEventType eventType)
    {
        OrganizationEventTypeContract.RequireDefined(eventType, nameof(eventType));
        return _subscribers.GetValueOrDefault(eventType, ImmutableArray<EventSubscriber>.Empty);
    }

    public sealed class Builder
    {
        private readonly OrganizationId _organizationId;
        private readonly Dictionary<PositionId, ImmutableArray<EventSubscription>> _positions = new();

        internal Builder(OrganizationId organizationId) => _organizationId = organizationId;

        public Builder AddPosition(PositionId positionId, IEnumerable<EventSubscription> subscriptions)
        {
            ArgumentNullException.ThrowIfNull(positionId);
            ArgumentNullException.ThrowIfNull(subscriptions);
            var declarations = subscriptions.ToImmutableArray();
            var identities = new HashSet<EventSubscriptionParameters>();
            foreach (var subscription in declarations)
            {
                ArgumentNullException.ThrowIfNull(subscription);
                if (!identities.Add(subscription.Parameters))
                    throw new ArgumentException("Duplicate subscription identity within a position.", nameof(subscriptions));
            }
            if (!_positions.TryAdd(positionId, declarations))
                throw new ArgumentException("The position was already added to the snapshot.", nameof(positionId));
            return this;
        }

        public EventSubscriptionsSnapshot Build()
        {
            var subscribers = _positions.SelectMany(position => position.Value.Select(
                    subscription => new EventSubscriber(position.Key, subscription)))
                .GroupBy(subscriber => subscriber.Subscription.EventType)
                .ToImmutableDictionary(group => group.Key, group => group
                    .OrderBy(subscriber => subscriber.PositionId.Value, StringComparer.Ordinal)
                    .ThenBy(subscriber => ParameterValue(subscriber.Subscription.Parameters))
                    .ToImmutableArray());
            return new(_organizationId, subscribers);
        }

        private static long ParameterValue(EventSubscriptionParameters parameters) => parameters switch
        {
            DirectiveDeadlineParameters deadline => deadline.Within.Ticks,
            PositionBlockedParameters blocked => blocked.After.Ticks,
            BudgetThresholdParameters budget => budget.ThresholdPercent,
            _ => throw new ArgumentException("Unknown subscription parameters.", nameof(parameters)),
        };
    }
}
