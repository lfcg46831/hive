using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>A subscriber-specific occurrence, ready for durable registration, not delivery.</summary>
public sealed record DomainEventOccurrence
{
    public DomainEventOccurrence(OrganizationId organizationId, EventSubscriber subscriber,
        OrganizationEventPayload payload, DateTimeOffset? windowEndsAtUtc = null)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(subscriber);
        ArgumentNullException.ThrowIfNull(payload);
        if (subscriber.Subscription.Parameters != payload.Parameters)
            throw new ArgumentException("Payload parameters must match the subscription.", nameof(payload));

        var end = windowEndsAtUtc?.ToUniversalTime();
        switch (payload)
        {
            case DirectiveDeadlineApproachingPayload deadline:
                if (end != deadline.DeadlineAtUtc)
                    throw new ArgumentException("Deadline window must end at the deadline.", nameof(windowEndsAtUtc));
                break;
            case PositionBlockedProlongedPayload blocked:
                if (blocked.SourcePositionId != subscriber.PositionId || end is not null)
                    throw new ArgumentException("Blocked occurrence must target its source with an open window.");
                break;
            case BudgetThresholdReachedPayload budget:
                if (budget.SourcePositionId != subscriber.PositionId || end is null)
                    throw new ArgumentException("Budget occurrence requires its source and a civil-day boundary.");
                var zone = TimeZoneInfo.FindSystemTimeZoneById(budget.TimeZone);
                DateOnly CivilDate(DateTimeOffset value) =>
                    DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(value, zone).DateTime);
                if (end <= payload.ObservedAtUtc || CivilDate(end.Value.AddTicks(-1)) != budget.CivilDate
                    || CivilDate(end.Value) <= budget.CivilDate
                    || CivilDate(payload.ObservedAtUtc) != budget.CivilDate)
                    throw new ArgumentException("Budget window must end exactly at its civil-day boundary.", nameof(windowEndsAtUtc));
                break;
            default:
                throw new ArgumentException("Unknown organization event payload.", nameof(payload));
        }
        if (end is { } limit && limit <= payload.ObservedAtUtc)
            throw new ArgumentException("Observation must precede the exclusive window end.", nameof(windowEndsAtUtc));

        OrganizationId = organizationId;
        Subscriber = subscriber;
        Payload = payload;
        WindowEndsAtUtc = end;
        Key = DomainEventIdempotencyKey.From(organizationId, subscriber.PositionId, payload);
    }

    public OrganizationId OrganizationId { get; }
    public EventSubscriber Subscriber { get; }
    public OrganizationEventPayload Payload { get; }
    public DomainEventIdempotencyKey Key { get; }
    public DateTimeOffset WindowStartsAtUtc => Payload.OccurredAtUtc;
    public DateTimeOffset? WindowEndsAtUtc { get; }
}
