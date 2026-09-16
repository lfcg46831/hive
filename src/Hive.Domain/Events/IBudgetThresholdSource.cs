using System.Collections.Immutable;
using System.Numerics;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Budget category of one persisted cost fact, derived from the message that caused it.</summary>
public enum BudgetCostCategory
{
    /// <summary>Organizational messages other than Pulse/EventTrigger.</summary>
    Reactive = 1,

    /// <summary>Pulse or EventTrigger (§4.6).</summary>
    Proactive = 2,

    /// <summary>The accepted message type is not persisted yet; the cost only counts towards total.</summary>
    Unclassified = 3,
}

/// <summary>One attempt-scoped EUR cost fact, already deduplicated by attempt identity.</summary>
public sealed record BudgetCostFact
{
    public BudgetCostFact(EventSourceCorrelation correlation, string attemptIdentity,
        DateTimeOffset occurredAtUtc, decimal amountEur, BudgetCostCategory category)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentException.ThrowIfNullOrWhiteSpace(attemptIdentity);
        ArgumentOutOfRangeException.ThrowIfNegative(amountEur);
        if (!Enum.IsDefined(category))
            throw new ArgumentOutOfRangeException(nameof(category), category, "Unknown budget cost category.");
        if (occurredAtUtc == default)
            throw new ArgumentException("The cost instant must be specified.", nameof(occurredAtUtc));
        Correlation = correlation;
        AttemptIdentity = attemptIdentity;
        OccurredAtUtc = occurredAtUtc.ToUniversalTime();
        AmountEur = amountEur;
        Category = category;
    }

    public EventSourceCorrelation Correlation { get; }

    /// <summary>Operation, iteration and AttemptId of the source call; ordinal tie-breaker only.</summary>
    public string AttemptIdentity { get; }

    public DateTimeOffset OccurredAtUtc { get; }
    public decimal AmountEur { get; }
    public BudgetCostCategory Category { get; }

    public bool CountsTowards(DailyBudgetKind budget) => budget switch
    {
        DailyBudgetKind.Total => true,
        DailyBudgetKind.Reactive => Category == BudgetCostCategory.Reactive,
        DailyBudgetKind.Proactive => Category == BudgetCostCategory.Proactive,
        _ => throw new ArgumentOutOfRangeException(nameof(budget), budget, "Unknown daily budget."),
    };

    /// <summary>Business order, independent of allocated sequence ids and commit order.</summary>
    public static IComparer<BudgetCostFact> Order { get; } = Comparer<BudgetCostFact>.Create((left, right) =>
    {
        var result = left.OccurredAtUtc.UtcTicks.CompareTo(right.OccurredAtUtc.UtcTicks);
        if (result == 0) result = left.Correlation.ThreadId.Value.CompareTo(right.Correlation.ThreadId.Value);
        if (result == 0) result = left.Correlation.MessageId.Value.CompareTo(right.Correlation.MessageId.Value);
        return result != 0 ? result : StringComparer.Ordinal.Compare(left.AttemptIdentity, right.AttemptIdentity);
    });
}

/// <summary>A civil day in one timezone, with an inclusive UTC start and exclusive UTC end.</summary>
public sealed record CivilDay
{
    private static readonly TimeSpan SearchSpan = TimeSpan.FromDays(2);

    private CivilDay(DateOnly date, DateTimeOffset startsAtUtc, DateTimeOffset endsAtUtc)
    {
        Date = date;
        StartsAtUtc = startsAtUtc;
        EndsAtUtc = endsAtUtc;
    }

    public DateOnly Date { get; }
    public DateTimeOffset StartsAtUtc { get; }
    public DateTimeOffset EndsAtUtc { get; }

    public static DateOnly DateOf(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(instant, zone).DateTime);
    }

    /// <summary>Exact boundaries, including 23/25-hour days, found on UTC ticks.</summary>
    public static CivilDay Containing(DateTimeOffset instant, TimeZoneInfo zone)
    {
        ArgumentNullException.ThrowIfNull(zone);
        var utc = instant.ToUniversalTime();
        var date = DateOf(utc, zone);
        // First instant whose civil date is at least the date (start) or after it (end).
        var start = FirstInstant(utc.Add(-SearchSpan), utc, value => DateOf(value, zone) >= date);
        var end = FirstInstant(utc, utc.Add(SearchSpan), value => DateOf(value, zone) > date);
        return new(date, start, end);
    }

    public bool Contains(DateTimeOffset instant) => instant >= StartsAtUtc && instant < EndsAtUtc;

    private static DateTimeOffset FirstInstant(DateTimeOffset low, DateTimeOffset high,
        Func<DateTimeOffset, bool> predicate)
    {
        if (predicate(low) || !predicate(high))
            throw new InvalidOperationException("Civil day boundary is outside the supported search range.");
        var lowTicks = low.UtcTicks;
        var highTicks = high.UtcTicks;
        while (highTicks - lowTicks > 1)
        {
            var middle = lowTicks + (highTicks - lowTicks) / 2;
            if (predicate(new DateTimeOffset(middle, TimeSpan.Zero))) highTicks = middle;
            else lowTicks = middle;
        }
        return new DateTimeOffset(highTicks, TimeSpan.Zero);
    }
}

/// <summary>Exact percentage threshold arithmetic shared by detector and payload validation.</summary>
public static class BudgetThreshold
{
    /// <summary>True when consumed × 100 ≥ limit × percent, without decimal rounding or overflow.</summary>
    public static bool IsReached(decimal consumedEur, decimal limitEur, int thresholdPercent) =>
        ScaledAmount(consumedEur) * 100 >= ScaledAmount(limitEur) * thresholdPercent;

    private static BigInteger ScaledAmount(decimal amount)
    {
        var bits = decimal.GetBits(amount);
        var coefficient = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        var scale = (bits[3] >> 16) & 0xff;
        var value = coefficient * BigInteger.Pow(10, 28 - scale);
        return amount < 0 ? -value : value;
    }
}

/// <summary>
/// Current civil-day EUR consumption of one subscribing position with at least one declared positive
/// daily cap, read from a single persisted snapshot of the registry and the audit log.
/// </summary>
public sealed record PositionDailyBudgetUsage
{
    public PositionDailyBudgetUsage(OrganizationId organizationId, PositionId positionId, string timeZone,
        CivilDay day, decimal? reactiveLimitEur, decimal? proactiveLimitEur, decimal? totalLimitEur,
        IEnumerable<BudgetCostFact> facts)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        ArgumentNullException.ThrowIfNull(day);
        ArgumentNullException.ThrowIfNull(facts);
        if (timeZone != timeZone.Trim())
            throw new ArgumentException("Timezone must not have surrounding whitespace.", nameof(timeZone));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        if (CivilDay.Containing(day.StartsAtUtc, zone) != day)
            throw new ArgumentException("The civil day does not belong to the timezone.", nameof(day));
        foreach (var limit in new[] { reactiveLimitEur, proactiveLimitEur, totalLimitEur })
            if (limit is < 0m) throw new ArgumentOutOfRangeException(nameof(limit), limit, "Budget caps cannot be negative.");

        var ordered = facts.ToImmutableArray();
        foreach (var fact in ordered)
        {
            ArgumentNullException.ThrowIfNull(fact);
            if (!day.Contains(fact.OccurredAtUtc))
                throw new ArgumentException("Cost facts must belong to the civil day.", nameof(facts));
        }
        OrganizationId = organizationId;
        PositionId = positionId;
        TimeZone = timeZone;
        Day = day;
        ReactiveLimitEur = reactiveLimitEur;
        ProactiveLimitEur = proactiveLimitEur;
        TotalLimitEur = totalLimitEur;
        Facts = ordered.Sort(BudgetCostFact.Order);
    }

    public OrganizationId OrganizationId { get; }
    public PositionId PositionId { get; }
    public string TimeZone { get; }
    public CivilDay Day { get; }
    public decimal? ReactiveLimitEur { get; }
    public decimal? ProactiveLimitEur { get; }
    public decimal? TotalLimitEur { get; }
    public ImmutableArray<BudgetCostFact> Facts { get; }

    public decimal? LimitFor(DailyBudgetKind budget) => budget switch
    {
        DailyBudgetKind.Reactive => ReactiveLimitEur,
        DailyBudgetKind.Proactive => ProactiveLimitEur,
        DailyBudgetKind.Total => TotalLimitEur,
        _ => throw new ArgumentOutOfRangeException(nameof(budget), budget, "Unknown daily budget."),
    };
}

/// <summary>
/// Reads, for the requested subscribing positions, the civil day containing the evaluation instant
/// in the position timezone (UTC when undeclared) and its attempt-scoped EUR cost facts not later
/// than that instant. Positions without a declared positive cap, or absent from the registry, are
/// omitted. No feed cursor: late commits and configuration changes are revisited on every call.
/// Invalid persisted data, unresolvable declared timezones, unavailability and cancellation propagate.
/// </summary>
public interface IBudgetThresholdSource
{
    ValueTask<ImmutableArray<PositionDailyBudgetUsage>> ReadAsync(OrganizationId organizationId,
        IReadOnlyCollection<PositionId> positionIds, DateTimeOffset evaluatedAtUtc,
        CancellationToken cancellationToken = default);
}
