using Hive.Domain.Events;

namespace Hive.Application.Events;

public sealed class DirectiveDeadlineApproachingDetector : IDomainEventDetector
{
    private readonly IDirectiveDeadlineSource _source;

    public DirectiveDeadlineApproachingDetector(IDirectiveDeadlineSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public OrganizationEventType EventType => OrganizationEventType.DirectiveDeadlineApproaching;

    public async ValueTask<DomainEventDetectionResult> EvaluateAsync(DomainEventDetectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.DetectorId.EventType != EventType)
            throw new ArgumentException("Context must belong to the deadline detector.", nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Subscribers.IsEmpty) return new(null, []);

        var subscribers = context.Subscribers.ToLookup(item => item.PositionId);
        var candidates = await _source.ReadAsync(context.DetectorId.OrganizationId,
            subscribers.Select(group => group.Key).ToArray(), context.EvaluatedAtUtc, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var occurrences = new List<DomainEventOccurrence>();
        foreach (var candidate in candidates)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (candidate.OrganizationId != context.DetectorId.OrganizationId
                || !subscribers.Contains(candidate.PositionId))
                throw new InvalidOperationException("Deadline source returned a candidate outside the requested scope.");

            foreach (var subscriber in subscribers[candidate.PositionId])
            {
                var parameters = (DirectiveDeadlineParameters)subscriber.Subscription.Parameters;
                var remaining = candidate.DeadlineAtUtc - context.EvaluatedAtUtc;
                if (remaining <= TimeSpan.Zero || remaining > parameters.Within) continue;

                var payload = new DirectiveDeadlineApproachingPayload(candidate.Correlation,
                    candidate.DeadlineAtUtc, parameters, context.EvaluatedAtUtc);
                occurrences.Add(new(context.DetectorId.OrganizationId, subscriber, payload, candidate.DeadlineAtUtc));
            }
        }

        return new(null, occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal));
    }
}
