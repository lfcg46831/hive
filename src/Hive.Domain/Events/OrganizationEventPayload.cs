using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Xml;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

/// <summary>Closed v1 payload shapes; no detector, persistence or OrgMessage production.</summary>
public abstract record OrganizationEventPayload
{
    private protected OrganizationEventPayload(
        EventSubscriptionParameters parameters,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt,
        EventSourceCorrelation? correlation)
    {
        ArgumentNullException.ThrowIfNull(parameters);
        if (observedAt < occurredAt)
            throw new ArgumentException("Observation cannot precede the occurrence.", nameof(observedAt));
        Parameters = parameters;
        OccurredAtUtc = occurredAt.ToUniversalTime();
        ObservedAtUtc = observedAt.ToUniversalTime();
        Correlation = correlation;
    }

    public int SchemaVersion => 1;
    public OrganizationEventType EventType => Parameters.EventType;
    public EventSubscriptionParameters Parameters { get; }
    public DateTimeOffset OccurredAtUtc { get; }
    public DateTimeOffset ObservedAtUtc { get; }
    public EventSourceCorrelation? Correlation { get; }

    /// <summary>The canonical JSON payload, compatible with the existing deadline inbox consumer.</summary>
    public string ToJson()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("schema_version", SchemaVersion);
            writer.WriteString("occurred_at_utc", Instant(OccurredAtUtc));
            writer.WriteString("observed_at_utc", Instant(ObservedAtUtc));
            if (Correlation is { } source)
            {
                writer.WriteString("source_message_id", source.MessageId.ToString());
                writer.WriteString("source_thread_id", source.ThreadId.ToString());
                if (source.DirectiveId is { } directive)
                    writer.WriteString("directive_id", directive.ToString());
            }
            WriteFields(writer);
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private protected abstract void WriteFields(Utf8JsonWriter writer);
    internal abstract IEnumerable<string> IdentityComponents();

    internal static string Instant(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static string Date(DateOnly value) => value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
}

public sealed record DirectiveDeadlineApproachingPayload : OrganizationEventPayload
{
    public DirectiveDeadlineApproachingPayload(
        EventSourceCorrelation correlation,
        DateTimeOffset deadline,
        DirectiveDeadlineParameters parameters,
        DateTimeOffset observedAt)
        : base(parameters, deadline - (parameters ?? throw new ArgumentNullException(nameof(parameters))).Within, observedAt, correlation)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        ArgumentNullException.ThrowIfNull(correlation.DirectiveId);
        if (observedAt >= deadline)
            throw new ArgumentException("Deadline observation must be before the deadline.", nameof(observedAt));
        DeadlineAtUtc = deadline.ToUniversalTime();
    }

    public DateTimeOffset DeadlineAtUtc { get; }
    public TimeSpan Within => ((DirectiveDeadlineParameters)Parameters).Within;
    public TimeSpan Remaining => DeadlineAtUtc - ObservedAtUtc;

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("deadline_at_utc", Instant(DeadlineAtUtc));
        writer.WriteString("within", XmlConvert.ToString(Within));
        writer.WriteString("remaining", XmlConvert.ToString(Remaining));
    }

    internal override IEnumerable<string> IdentityComponents() =>
        [Correlation!.MessageId.ToString(), Instant(DeadlineAtUtc), Parameters.IdentityValue];
}

public sealed record PositionBlockedProlongedPayload : OrganizationEventPayload
{
    public PositionBlockedProlongedPayload(
        PositionId sourcePositionId,
        DateTimeOffset blockedSince,
        PositionBlockedParameters parameters,
        PositionBlockedCause cause,
        DateTimeOffset observedAt,
        EventSourceCorrelation? correlation = null)
        : base(parameters, blockedSince + (parameters ?? throw new ArgumentNullException(nameof(parameters))).After, observedAt, correlation)
    {
        ArgumentNullException.ThrowIfNull(sourcePositionId);
        _ = PositionBlockedCauseContract.ToWireValue(cause);
        if (cause == PositionBlockedCause.PendingEscalation && correlation is null)
            throw new ArgumentException("A pending escalation requires source correlation.", nameof(correlation));
        SourcePositionId = sourcePositionId;
        BlockedSinceUtc = blockedSince.ToUniversalTime();
        Cause = cause;
    }

    public PositionId SourcePositionId { get; }
    public DateTimeOffset BlockedSinceUtc { get; }
    public TimeSpan After => ((PositionBlockedParameters)Parameters).After;
    public TimeSpan BlockedDuration => ObservedAtUtc - BlockedSinceUtc;
    public PositionBlockedCause Cause { get; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("source_position_id", SourcePositionId.Value);
        writer.WriteString("blocked_since_utc", Instant(BlockedSinceUtc));
        writer.WriteString("after", XmlConvert.ToString(After));
        writer.WriteString("blocked_duration", XmlConvert.ToString(BlockedDuration));
        writer.WriteString("cause", PositionBlockedCauseContract.ToWireValue(Cause));
    }

    internal override IEnumerable<string> IdentityComponents() =>
        [SourcePositionId.Value, Instant(BlockedSinceUtc), Parameters.IdentityValue];
}

public sealed record BudgetThresholdReachedPayload : OrganizationEventPayload
{
    public BudgetThresholdReachedPayload(
        PositionId sourcePositionId,
        EventSourceCorrelation correlation,
        DailyBudgetKind budget,
        DateOnly civilDate,
        string timeZone,
        BudgetThresholdParameters parameters,
        decimal limitEur,
        decimal consumedEur,
        DateTimeOffset occurredAt,
        DateTimeOffset observedAt)
        : base(parameters, occurredAt, observedAt, correlation)
    {
        ArgumentNullException.ThrowIfNull(sourcePositionId);
        ArgumentNullException.ThrowIfNull(correlation);
        _ = DailyBudgetKindContract.ToWireValue(budget);
        ArgumentException.ThrowIfNullOrWhiteSpace(timeZone);
        if (timeZone != timeZone.Trim())
            throw new ArgumentException("Timezone must not have surrounding whitespace.", nameof(timeZone));
        var zone = TimeZoneInfo.FindSystemTimeZoneById(timeZone);
        if (DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(OccurredAtUtc, zone).DateTime) != civilDate)
            throw new ArgumentException("The occurrence must belong to the budget civil day.", nameof(civilDate));
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(limitEur, 0m);
        ArgumentOutOfRangeException.ThrowIfNegative(consumedEur);
        // Compare exact scaled integers: decimal multiplication can overflow or round tiny caps.
        if (ScaledAmount(consumedEur) * 100 < ScaledAmount(limitEur) * parameters.ThresholdPercent)
            throw new ArgumentException("Observed consumption has not reached the threshold.", nameof(consumedEur));
        SourcePositionId = sourcePositionId;
        Budget = budget;
        CivilDate = civilDate;
        TimeZone = timeZone;
        LimitEur = limitEur;
        ConsumedEur = consumedEur;
    }

    public PositionId SourcePositionId { get; }
    public DailyBudgetKind Budget { get; }
    public DateOnly CivilDate { get; }
    public string TimeZone { get; }
    public int ThresholdPercent => ((BudgetThresholdParameters)Parameters).ThresholdPercent;
    public decimal LimitEur { get; }
    public decimal ConsumedEur { get; }

    private protected override void WriteFields(Utf8JsonWriter writer)
    {
        writer.WriteString("source_position_id", SourcePositionId.Value);
        writer.WriteString("budget", DailyBudgetKindContract.ToWireValue(Budget));
        writer.WriteString("civil_date", Date(CivilDate));
        writer.WriteString("time_zone", TimeZone);
        writer.WriteNumber("threshold_percent", ThresholdPercent);
        writer.WriteNumber("limit_eur", LimitEur);
        writer.WriteNumber("consumed_eur", ConsumedEur);
    }

    internal override IEnumerable<string> IdentityComponents() =>
        [SourcePositionId.Value, DailyBudgetKindContract.ToWireValue(Budget), TimeZone, Date(CivilDate), Parameters.IdentityValue];

    private static BigInteger ScaledAmount(decimal amount)
    {
        var bits = decimal.GetBits(amount);
        var coefficient = ((BigInteger)(uint)bits[2] << 64) | ((BigInteger)(uint)bits[1] << 32) | (uint)bits[0];
        var scale = (bits[3] >> 16) & 0xff;
        return coefficient * BigInteger.Pow(10, 28 - scale);
    }
}
