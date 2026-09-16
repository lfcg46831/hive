using Hive.Domain.Events;
using Hive.Domain.Identity;

namespace Hive.Application.Events;

public sealed class BudgetThresholdReachedDetector : IDomainEventDetector
{
    private static readonly DailyBudgetKind[] Budgets =
        [DailyBudgetKind.Reactive, DailyBudgetKind.Proactive, DailyBudgetKind.Total];

    private readonly IBudgetThresholdSource _source;

    public BudgetThresholdReachedDetector(IBudgetThresholdSource source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    public OrganizationEventType EventType => OrganizationEventType.BudgetThresholdReached;

    public async ValueTask<DomainEventDetectionResult> EvaluateAsync(DomainEventDetectionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (context.DetectorId.EventType != EventType)
            throw new ArgumentException("Context must belong to the budget threshold detector.", nameof(context));
        cancellationToken.ThrowIfCancellationRequested();
        if (context.Subscribers.IsEmpty) return new(null, []);

        var subscribers = context.Subscribers.ToLookup(item => item.PositionId);
        var usages = await _source.ReadAsync(context.DetectorId.OrganizationId,
            subscribers.Select(group => group.Key).ToArray(), context.EvaluatedAtUtc, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var occurrences = new List<DomainEventOccurrence>();
        var seen = new HashSet<PositionId>();
        foreach (var usage in usages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (usage.OrganizationId != context.DetectorId.OrganizationId
                || !subscribers.Contains(usage.PositionId) || !seen.Add(usage.PositionId)
                || !usage.Day.Contains(context.EvaluatedAtUtc)
                || usage.Facts.Any(fact => fact.OccurredAtUtc > context.EvaluatedAtUtc))
                throw new InvalidOperationException("Budget source returned usage outside the requested scope.");

            foreach (var budget in Budgets)
            {
                if (usage.LimitFor(budget) is not { } limit || limit <= 0m) continue;
                foreach (var subscriber in subscribers[usage.PositionId])
                {
                    var parameters = (BudgetThresholdParameters)subscriber.Subscription.Parameters;
                    if (Crossing(usage, budget, limit, parameters.ThresholdPercent) is not { } crossing)
                        continue;
                    var payload = new BudgetThresholdReachedPayload(usage.PositionId, crossing.Fact.Correlation, budget,
                        usage.Day.Date, usage.TimeZone, parameters, limit, crossing.Consumed, crossing.Fact.OccurredAtUtc,
                        context.EvaluatedAtUtc);
                    occurrences.Add(new(context.DetectorId.OrganizationId, subscriber, payload, usage.Day.EndsAtUtc));
                }
            }
        }

        return new(null, occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal));
    }

    /// <summary>The first fact, in business order, at which the running sum reaches the threshold.</summary>
    private static (BudgetCostFact Fact, decimal Consumed)? Crossing(PositionDailyBudgetUsage usage,
        DailyBudgetKind budget, decimal limit, int thresholdPercent)
    {
        var consumed = 0m;
        foreach (var fact in usage.Facts)
        {
            if (!fact.CountsTowards(budget)) continue;
            consumed += fact.AmountEur;
            if (BudgetThreshold.IsReached(consumed, limit, thresholdPercent)) return (fact, consumed);
        }
        return null;
    }
}
