using Hive.Domain.Events;
using Hive.Domain.Identity;

namespace Hive.Application.Events;

public sealed class PositionBlockedProlongedDetector : IDomainEventDetector
{
    private readonly IPositionBlockedSource _source;

    public PositionBlockedProlongedDetector(IPositionBlockedSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public OrganizationEventType EventType => OrganizationEventType.PositionBlockedProlonged;

    public async ValueTask<DomainEventDetectionResult> EvaluateAsync(DomainEventDetectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.DetectorId.EventType != EventType)
            throw new ArgumentException("Context must belong to the blocked-position detector.", nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Subscribers.IsEmpty) return new(null, []);

        var subscribers = context.Subscribers.ToLookup(item => item.PositionId);
        var periods = await _source.ReadAsync(context.DetectorId.OrganizationId,
            subscribers.Select(group => group.Key).ToArray(), cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var occurrences = new List<DomainEventOccurrence>();
        var seen = new HashSet<PositionId>();
        foreach (var period in periods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (period.OrganizationId != context.DetectorId.OrganizationId
                || !subscribers.Contains(period.PositionId) || !seen.Add(period.PositionId))
                throw new InvalidOperationException("Blocked source returned an invalid current-period scope.");

            foreach (var subscriber in subscribers[period.PositionId])
            {
                var parameters = (PositionBlockedParameters)subscriber.Subscription.Parameters;
                if (context.EvaluatedAtUtc - period.BlockedSinceUtc < parameters.After) continue;
                var payload = new PositionBlockedProlongedPayload(period.PositionId, period.BlockedSinceUtc,
                    parameters, period.Cause, context.EvaluatedAtUtc, period.Correlation);
                occurrences.Add(new(context.DetectorId.OrganizationId, subscriber, payload));
            }
        }
        return new(null, occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal));
    }
}
