using System.Globalization;
using System.Text.Json;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class OrganizationEventDomainModelTests
{
    private static readonly OrganizationId Org = OrganizationId.From("acme");
    private static readonly PositionId Subscriber = PositionId.From("lead");
    private static readonly PositionId Source = PositionId.From("engineer");
    private static readonly DateTimeOffset At = new(2026, 9, 13, 10, 0, 0, TimeSpan.Zero);
    private static readonly EventSourceCorrelation Correlation = new(
        MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000001")),
        ThreadId.From(Guid.Parse("20000000-0000-0000-0000-000000000001")),
        DirectiveId.From(Guid.Parse("30000000-0000-0000-0000-000000000001")));

    [Theory]
    [InlineData(OrganizationEventType.DirectiveDeadlineApproaching, "directive-deadline-approaching")]
    [InlineData(OrganizationEventType.PositionBlockedProlonged, "position-blocked-prolonged")]
    [InlineData(OrganizationEventType.BudgetThresholdReached, "budget-threshold-reached")]
    public void Catalog_round_trips_only_canonical_names(OrganizationEventType type, string wire)
    {
        Assert.Equal(wire, OrganizationEventTypeContract.ToWireValue(type));
        Assert.Equal(type, OrganizationEventTypeContract.ParseWireValue(wire));
        Assert.True(OrganizationEventTypeContract.TryParseWireValue(wire, out var parsed));
        Assert.Equal(type, parsed);
        Assert.Equal(type, OrganizationEventTypeContract.RequireDefined(type, "type"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("DirectiveDeadlineApproaching")]
    [InlineData("Directive-deadline-approaching")]
    [InlineData("directive-deadline-approaching ")]
    [InlineData(" budget-threshold-reached")]
    [InlineData("1")]
    [InlineData("budget-exhausted")]
    public void Unknown_or_noncanonical_event_names_fail_closed(string? wire)
    {
        Assert.False(OrganizationEventTypeContract.TryParseWireValue(wire, out var type));
        Assert.Equal(default, type);
        Assert.ThrowsAny<ArgumentException>(() => OrganizationEventTypeContract.ParseWireValue(wire!));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(4)]
    public void Undefined_enums_are_rejected(int value)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => OrganizationEventTypeContract.ToWireValue((OrganizationEventType)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => OrganizationEventTypeContract.RequireDefined((OrganizationEventType)value, "type"));
        Assert.Throws<ArgumentOutOfRangeException>(() => Blocked(cause: (PositionBlockedCause)value));
        Assert.Throws<ArgumentOutOfRangeException>(() => Budget(budget: (DailyBudgetKind)value));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(long.MinValue)]
    public void Duration_parameters_reject_nonpositive_values(long ticks)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new DirectiveDeadlineParameters(TimeSpan.FromTicks(ticks)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositionBlockedParameters(TimeSpan.FromTicks(ticks)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(101)]
    public void Percentage_parameters_reject_values_outside_1_to_100(int percent) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new BudgetThresholdParameters(percent));

    [Fact]
    public void Subscriptions_are_typed_and_default_to_noncritical_normal_priority()
    {
        EventSubscriptionParameters[] parameters =
        [new DirectiveDeadlineParameters(TimeSpan.FromTicks(1)), new PositionBlockedParameters(TimeSpan.MaxValue), new BudgetThresholdParameters(1)];
        Assert.Equal(3, Enum.GetValues<OrganizationEventType>().Length);
        foreach (var parameter in parameters)
        {
            var subscription = new EventSubscription(parameter);
            Assert.False(subscription.IsCritical);
            Assert.Equal(Priority.Normal, subscription.Priority);
            Assert.Equal(parameter.EventType, subscription.EventType);
            Assert.Same(parameter, subscription.Parameters);
        }
        var critical = new EventSubscription(new BudgetThresholdParameters(100), true, Priority.High);
        Assert.True(critical.IsCritical);
        Assert.Equal(Priority.High, critical.Priority);
        Assert.Equal(new BudgetThresholdParameters(100), critical.Parameters);
        Assert.Throws<ArgumentNullException>(() => new EventSubscription(null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EventSubscription(parameters[0], priority: (Priority)99));
    }

    [Fact]
    public void Deadline_payload_preserves_correlation_and_the_half_open_window()
    {
        var payload = Deadline();
        Assert.Equal(At, payload.OccurredAtUtc);
        Assert.Equal(TimeSpan.FromMinutes(30), payload.Remaining);
        using var document = JsonDocument.Parse(payload.ToJson());
        var json = document.RootElement;
        Assert.Equal(1, json.GetProperty("schema_version").GetInt32());
        Assert.Equal(Correlation.MessageId.Value, json.GetProperty("source_message_id").GetGuid());
        Assert.Equal(Correlation.ThreadId.Value, json.GetProperty("source_thread_id").GetGuid());
        Assert.Equal(Correlation.DirectiveId!.Value, json.GetProperty("directive_id").GetGuid());
        Assert.Equal("2026-09-13T11:00:00.0000000Z", json.GetProperty("deadline_at_utc").GetString());
        Assert.Equal("2026-09-13T10:00:00.0000000Z", json.GetProperty("occurred_at_utc").GetString());
        Assert.Equal("2026-09-13T10:30:00.0000000Z", json.GetProperty("observed_at_utc").GetString());
        Assert.Equal("PT1H", json.GetProperty("within").GetString());
        Assert.Equal("PT30M", json.GetProperty("remaining").GetString());
        Assert.Equal(9, json.EnumerateObject().Count());
        Assert.Equal(TimeSpan.FromHours(1), Deadline(observedAt: At).Remaining);
        Assert.Throws<ArgumentException>(() => Deadline(observedAt: At.AddTicks(-1)));
        Assert.Throws<ArgumentException>(() => Deadline(observedAt: At.AddHours(1)));
        Assert.Throws<ArgumentNullException>(() => Deadline(correlation: new(Correlation.MessageId, Correlation.ThreadId)));
    }

    [Fact]
    public void Blocked_payload_requires_escalation_correlation_but_allows_configuration_without_a_message()
    {
        var payload = Blocked();
        Assert.Equal(At.AddHours(1), payload.OccurredAtUtc);
        Assert.Equal(TimeSpan.FromHours(2), payload.BlockedDuration);
        using var document = JsonDocument.Parse(payload.ToJson());
        var json = document.RootElement;
        Assert.Equal("configuration-blocked", json.GetProperty("cause").GetString());
        Assert.Equal(Source.Value, json.GetProperty("source_position_id").GetString());
        Assert.Equal("2026-09-13T10:00:00.0000000Z", json.GetProperty("blocked_since_utc").GetString());
        Assert.Equal("PT1H", json.GetProperty("after").GetString());
        Assert.Equal("PT2H", json.GetProperty("blocked_duration").GetString());
        Assert.False(json.TryGetProperty("source_message_id", out _));
        Assert.Equal(8, json.EnumerateObject().Count());
        Assert.Throws<ArgumentException>(() => Blocked(cause: PositionBlockedCause.PendingEscalation));
        var escalation = Blocked(cause: PositionBlockedCause.PendingEscalation, correlation: Correlation);
        Assert.Same(Correlation, escalation.Correlation);
        Assert.Equal("pending-escalation", PositionBlockedCauseContract.ToWireValue(escalation.Cause));
        Assert.Equal(TimeSpan.FromHours(1), Blocked(observedAt: At.AddHours(1)).BlockedDuration);
        Assert.Throws<ArgumentException>(() => Blocked(observedAt: At.AddHours(1).AddTicks(-1)));
    }

    [Fact]
    public void Budget_payload_preserves_decimals_and_accepts_exact_threshold_and_overshoot()
    {
        var payload = Budget(consumed: 8.123456789m);
        using var document = JsonDocument.Parse(payload.ToJson());
        var json = document.RootElement;
        Assert.Equal("proactive", json.GetProperty("budget").GetString());
        Assert.Equal("2026-09-13", json.GetProperty("civil_date").GetString());
        Assert.Equal("Europe/Lisbon", json.GetProperty("time_zone").GetString());
        Assert.Equal(80, json.GetProperty("threshold_percent").GetInt32());
        Assert.Equal(10m, json.GetProperty("limit_eur").GetDecimal());
        Assert.Equal(8.123456789m, json.GetProperty("consumed_eur").GetDecimal());
        Assert.Equal(Correlation.MessageId.Value, json.GetProperty("source_message_id").GetGuid());
        Assert.Equal(13, json.EnumerateObject().Count());
        Assert.Equal(8m, Budget().ConsumedEur);
        Assert.Equal(11m, Budget(consumed: 11m).ConsumedEur);
        Assert.Equal(decimal.MaxValue, Budget(limit: decimal.MaxValue, consumed: decimal.MaxValue).LimitEur);
        Assert.Throws<ArgumentException>(() => Budget(consumed: 7.999999999m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Budget(consumed: -1m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Budget(limit: 0m));
        Assert.Throws<ArgumentOutOfRangeException>(() => Budget(limit: -1m));
        Assert.Throws<ArgumentException>(() => Budget(observedAt: At.AddTicks(-1)));
    }

    [Fact]
    public void Percentage_comparison_does_not_round_sub_decimal_thresholds_down()
    {
        const decimal smallest = 0.0000000000000000000000000001m;
        Assert.Throws<ArgumentException>(() => Budget(limit: smallest, consumed: 0m, percent: 1));
        Assert.Throws<ArgumentException>(() => Budget(limit: 3m * smallest, consumed: smallest, percent: 50));
        Assert.Equal(2m * smallest, Budget(limit: 3m * smallest, consumed: 2m * smallest, percent: 50).ConsumedEur);
        Assert.Equal(10m, Budget(percent: 100, consumed: 10m).ConsumedEur);
    }

    [Fact]
    public void Budget_civil_day_uses_position_timezone_including_dst_and_local_midnight()
    {
        var summerMidnight = new DateTimeOffset(2026, 9, 12, 23, 30, 0, TimeSpan.Zero);
        Assert.Equal(new DateOnly(2026, 9, 13), Budget(occurredAt: summerMidnight).CivilDate);
        Assert.Throws<ArgumentException>(() => Budget(occurredAt: summerMidnight, timeZone: "UTC"));
        var dstRepeat = new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero);
        var date = new DateOnly(2026, 10, 25);
        Assert.Equal(Key(Budget(occurredAt: dstRepeat, observedAt: dstRepeat, date: date)),
            Key(Budget(occurredAt: dstRepeat.AddHours(-1), observedAt: dstRepeat, date: date)));
        Assert.Throws<TimeZoneNotFoundException>(() => Budget(timeZone: "Invalid/Zone"));
        Assert.Throws<ArgumentException>(() => Budget(timeZone: " Europe/Lisbon"));
    }

    [Fact]
    public void Replay_changes_observations_without_changing_occurrence_identity()
    {
        Assert.Equal(Key(Deadline()), Key(Deadline(observedAt: At.AddMinutes(50))));
        Assert.Equal(Key(Blocked()), Key(Blocked(observedAt: At.AddDays(1), cause: PositionBlockedCause.PendingEscalation, correlation: Correlation)));
        Assert.Equal(Key(Budget()), Key(Budget(consumed: 9m, limit: 11m, occurredAt: At.AddMinutes(1), observedAt: At.AddHours(1),
            correlation: new EventSourceCorrelation(MessageId.New(), ThreadId.New()))));
        Assert.Equal(3, new[] { Key(Deadline()), Key(Blocked()), Key(Budget()) }.Distinct().Count());
    }

    [Fact]
    public void Every_identity_component_is_isolated()
    {
        var deadline = Key(Deadline());
        Assert.NotEqual(deadline, DomainEventIdempotencyKey.From(OrganizationId.From("other"), Subscriber, Deadline()));
        Assert.NotEqual(deadline, DomainEventIdempotencyKey.From(Org, PositionId.From("other"), Deadline()));
        Assert.NotEqual(deadline, Key(Deadline(correlation: new(MessageId.New(), Correlation.ThreadId, Correlation.DirectiveId))));
        Assert.NotEqual(deadline, Key(Deadline(deadline: At.AddHours(1).AddTicks(1))));
        Assert.NotEqual(deadline, Key(Deadline(within: TimeSpan.FromMinutes(45))));
        var blocked = Key(Blocked());
        Assert.NotEqual(blocked, Key(Blocked(source: Subscriber)));
        Assert.NotEqual(blocked, Key(Blocked(since: At.AddTicks(1))));
        Assert.NotEqual(blocked, Key(Blocked(after: TimeSpan.FromMinutes(45))));
        var budget = Key(Budget());
        Assert.NotEqual(budget, Key(Budget(source: Subscriber)));
        Assert.NotEqual(budget, Key(Budget(budget: DailyBudgetKind.Reactive)));
        Assert.NotEqual(budget, Key(Budget(timeZone: "UTC")));
        Assert.NotEqual(budget, Key(Budget(percent: 79)));
        Assert.NotEqual(budget, Key(Budget(date: new DateOnly(2026, 9, 14), occurredAt: At.AddDays(1), observedAt: At.AddDays(1))));
    }

    [Fact]
    public void Key_framing_has_a_fixed_vector_and_prevents_delimiter_collisions()
    {
        Assert.Equal("hive:domain-events:occurrence:v1:4:acme4:lead30:directive-deadline-approaching" +
            "36:10000000-0000-0000-0000-00000000000128:2026-09-13T11:00:00.0000000Z11:36000000000", Key(Deadline()).Value);
        Assert.NotEqual(
            DomainEventIdempotencyKey.From(OrganizationId.From("a/b"), PositionId.From("c"), Deadline()),
            DomainEventIdempotencyKey.From(OrganizationId.From("a"), PositionId.From("b/c"), Deadline()));
        Assert.NotEqual(
            DomainEventIdempotencyKey.From(OrganizationId.From("a:1"), PositionId.From("é🙂"), Deadline()),
            DomainEventIdempotencyKey.From(OrganizationId.From("a"), PositionId.From("1:é🙂"), Deadline()));
        Assert.Equal(Key(Deadline()).Value, Key(Deadline()).ToString());
    }

    [Theory]
    [InlineData("pt-PT")]
    [InlineData("ar-SA")]
    [InlineData("tr-TR")]
    public void Keys_and_payloads_are_stable_across_cultures_and_equivalent_offsets(string culture)
    {
        var expectedKey = Key(Deadline());
        var expectedJson = Deadline().ToJson();
        var expectedBudgetKey = Key(Budget());
        var expectedBudgetJson = Budget().ToJson();
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(culture);
            var equivalent = Deadline(deadline: At.AddHours(1).ToOffset(TimeSpan.FromHours(5.5)),
                observedAt: At.AddMinutes(30).ToOffset(TimeSpan.FromHours(-4)));
            Assert.Equal(expectedKey, Key(equivalent));
            Assert.Equal(expectedJson, equivalent.ToJson());
            Assert.Equal(expectedBudgetKey, Key(Budget()));
            Assert.Equal(expectedBudgetJson, Budget().ToJson());
            Assert.Equal(TimeSpan.Zero, equivalent.ObservedAtUtc.Offset);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    [Fact]
    public void Null_identities_and_parameters_cannot_produce_payloads_or_keys()
    {
        Assert.Throws<ArgumentNullException>(() => new EventSourceCorrelation(null!, Correlation.ThreadId));
        Assert.Throws<ArgumentNullException>(() => new EventSourceCorrelation(Correlation.MessageId, null!));
        Assert.Throws<ArgumentNullException>(() => new DirectiveDeadlineApproachingPayload(null!, At.AddHours(1), new(TimeSpan.FromHours(1)), At));
        Assert.Throws<ArgumentNullException>(() => new DirectiveDeadlineApproachingPayload(Correlation, At.AddHours(1), null!, At));
        Assert.Throws<ArgumentNullException>(() => new PositionBlockedProlongedPayload(null!, At, new(TimeSpan.FromHours(1)), PositionBlockedCause.ConfigurationBlocked, At.AddHours(1)));
        Assert.Throws<ArgumentNullException>(() => new PositionBlockedProlongedPayload(Source, At, null!, PositionBlockedCause.ConfigurationBlocked, At.AddHours(1)));
        Assert.Throws<ArgumentNullException>(() => DomainEventIdempotencyKey.From(null!, Subscriber, Deadline()));
        Assert.Throws<ArgumentNullException>(() => DomainEventIdempotencyKey.From(Org, null!, Deadline()));
        Assert.Throws<ArgumentNullException>(() => DomainEventIdempotencyKey.From(Org, Subscriber, null!));
    }

    [Fact]
    public void Cause_and_budget_wire_catalogs_round_trip_and_reject_aliases()
    {
        foreach (var cause in Enum.GetValues<PositionBlockedCause>())
            Assert.Equal(cause, PositionBlockedCauseContract.ParseWireValue(PositionBlockedCauseContract.ToWireValue(cause)));
        foreach (var budget in Enum.GetValues<DailyBudgetKind>())
            Assert.Equal(budget, DailyBudgetKindContract.ParseWireValue(DailyBudgetKindContract.ToWireValue(budget)));
        foreach (var invalid in new[] { "", "1", "Proactive", "proactive ", "max-calls-per-hour" })
            Assert.Throws<ArgumentException>(() => DailyBudgetKindContract.ParseWireValue(invalid));
        foreach (var invalid in new[] { "", "1", "ConfigurationBlocked", "configuration-blocked " })
            Assert.Throws<ArgumentException>(() => PositionBlockedCauseContract.ParseWireValue(invalid));
    }

    private static DomainEventIdempotencyKey Key(OrganizationEventPayload payload) => DomainEventIdempotencyKey.From(Org, Subscriber, payload);

    private static DirectiveDeadlineApproachingPayload Deadline(
        EventSourceCorrelation? correlation = null, DateTimeOffset? deadline = null, TimeSpan? within = null, DateTimeOffset? observedAt = null) =>
        new(correlation ?? Correlation, deadline ?? At.AddHours(1), new(within ?? TimeSpan.FromHours(1)), observedAt ?? At.AddMinutes(30));

    private static PositionBlockedProlongedPayload Blocked(
        PositionId? source = null, DateTimeOffset? since = null, TimeSpan? after = null,
        PositionBlockedCause cause = PositionBlockedCause.ConfigurationBlocked, DateTimeOffset? observedAt = null, EventSourceCorrelation? correlation = null) =>
        new(source ?? Source, since ?? At, new(after ?? TimeSpan.FromHours(1)), cause, observedAt ?? At.AddHours(2), correlation);

    private static BudgetThresholdReachedPayload Budget(
        PositionId? source = null, EventSourceCorrelation? correlation = null, DailyBudgetKind budget = DailyBudgetKind.Proactive,
        DateOnly? date = null, string timeZone = "Europe/Lisbon", int percent = 80, decimal limit = 10m, decimal consumed = 8m,
        DateTimeOffset? occurredAt = null, DateTimeOffset? observedAt = null) =>
        new(source ?? Source, correlation ?? Correlation, budget, date ?? new DateOnly(2026, 9, 13), timeZone,
            new(percent), limit, consumed, occurredAt ?? At, observedAt ?? At.AddHours(1));
}
