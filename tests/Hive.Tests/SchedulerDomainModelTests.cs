using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Scheduling;

namespace Hive.Tests;

public sealed class SchedulerDomainModelTests
{
    private static readonly OrganizationId Org = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("bug-triage");

    [Fact]
    public void ScheduleId_preserves_valid_value_and_renders_canonically()
    {
        var id = ScheduleId.From("daily-standup");

        Assert.Equal("daily-standup", id.Value);
        Assert.Equal("daily-standup", id.ToString());
        Assert.Equal(ScheduleId.From("daily-standup"), id);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    [InlineData("\t")]
    [InlineData(" lead")]
    [InlineData("trail ")]
    public void ScheduleId_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => ScheduleId.From(value));
    }

    [Fact]
    public void ScheduleId_rejects_null()
    {
        Assert.Throws<ArgumentNullException>(() => ScheduleId.From(null!));
    }

    [Fact]
    public void ScheduleId_rejects_reserved_separator()
    {
        // The '/' would break the composition of the deterministic idempotency key.
        Assert.Throws<ArgumentException>(() => ScheduleId.From("a/b"));
    }

    [Fact]
    public void CronExpression_preserves_declared_value()
    {
        Assert.Equal("0 9 * * MON-FRI", CronExpression.From("0 9 * * MON-FRI").Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData(" 0 9 * * *")]
    public void CronExpression_rejects_non_canonical_values(string value)
    {
        Assert.Throws<ArgumentException>(() => CronExpression.From(value));
    }

    [Fact]
    public void TemporalWindow_is_half_open_and_exposes_duration()
    {
        var start = new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);
        var end = new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero);
        var window = TemporalWindow.From(start, end);

        Assert.Equal(TimeSpan.FromHours(1), window.Duration);
        Assert.True(window.Contains(start));           // start inclusive
        Assert.False(window.Contains(end));            // end exclusive
        Assert.True(window.Contains(start.AddMinutes(30)));
        Assert.False(window.Contains(start.AddMinutes(-1)));
    }

    [Fact]
    public void TemporalWindow_rejects_zero_length_and_inverted_windows()
    {
        var instant = new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);

        Assert.Throws<ArgumentException>(() => TemporalWindow.From(instant, instant));
        Assert.Throws<ArgumentException>(() => TemporalWindow.From(instant, instant.AddHours(-1)));
    }

    [Fact]
    public void TemporalWindow_canonical_form_is_offset_independent()
    {
        var utc = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero));
        var shifted = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 4, 11, 0, 0, TimeSpan.FromHours(2)),  // same instant as 09:00Z
            new DateTimeOffset(2026, 7, 4, 12, 0, 0, TimeSpan.FromHours(2)));  // same instant as 10:00Z

        Assert.Equal(utc.ToCanonicalString(), shifted.ToCanonicalString());
    }

    [Fact]
    public void CatchUpPolicy_wire_contract_round_trips()
    {
        Assert.Equal("skip", CatchUpPolicyContract.ToWireValue(CatchUpPolicy.Skip));
        Assert.Equal("catch-up-once", CatchUpPolicyContract.ToWireValue(CatchUpPolicy.CatchUpOnce));
        Assert.Equal(CatchUpPolicy.Skip, CatchUpPolicyContract.ParseWireValue("skip"));
        Assert.Equal(CatchUpPolicy.CatchUpOnce, CatchUpPolicyContract.ParseWireValue("catch-up-once"));
        Assert.True(CatchUpPolicyContract.TryParseWireValue("skip", out _));
        Assert.False(CatchUpPolicyContract.TryParseWireValue("nope", out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => CatchUpPolicyContract.RequireDefined((CatchUpPolicy)99, "p"));
    }

    [Fact]
    public void ScheduleDefinition_creates_with_valid_components()
    {
        var definition = ScheduleDefinition.Create(
            ScheduleId.From("nightly-report"),
            CronExpression.From("0 2 * * *"),
            "Europe/Lisbon",
            "Produce the nightly report.",
            Priority.Normal,
            isCritical: false,
            CatchUpPolicy.Skip);

        Assert.Equal("nightly-report", definition.Id.Value);
        Assert.Equal("Europe/Lisbon", definition.TimeZone);
        Assert.Equal(Priority.Normal, definition.Priority);
        Assert.False(definition.IsCritical);
        Assert.Equal(CatchUpPolicy.Skip, definition.CatchUp);
    }

    [Fact]
    public void ScheduleDefinition_rejects_catch_up_on_non_critical_schedule()
    {
        Assert.Throws<ArgumentException>(() => ScheduleDefinition.Create(
            ScheduleId.From("nightly-report"),
            CronExpression.From("0 2 * * *"),
            "Europe/Lisbon",
            "Produce the nightly report.",
            Priority.Normal,
            isCritical: false,
            CatchUpPolicy.CatchUpOnce));
    }

    [Fact]
    public void ScheduleDefinition_allows_catch_up_on_critical_schedule()
    {
        var definition = ScheduleDefinition.Create(
            ScheduleId.From("payroll-run"),
            CronExpression.From("0 6 1 * *"),
            "Europe/Lisbon",
            "Run payroll.",
            Priority.Critical,
            isCritical: true,
            CatchUpPolicy.CatchUpOnce);

        Assert.True(definition.IsCritical);
        Assert.Equal(CatchUpPolicy.CatchUpOnce, definition.CatchUp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ScheduleDefinition_rejects_blank_payload_and_timezone(string blank)
    {
        Assert.Throws<ArgumentException>(() => ScheduleDefinition.Create(
            ScheduleId.From("s"),
            CronExpression.From("0 2 * * *"),
            blank,
            "payload",
            Priority.Normal,
            isCritical: false,
            CatchUpPolicy.Skip));

        Assert.Throws<ArgumentException>(() => ScheduleDefinition.Create(
            ScheduleId.From("s"),
            CronExpression.From("0 2 * * *"),
            "Europe/Lisbon",
            blank,
            Priority.Normal,
            isCritical: false,
            CatchUpPolicy.Skip));
    }

    [Fact]
    public void IdempotencyKey_is_deterministic_for_same_tuple()
    {
        var window = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero));
        var schedule = ScheduleId.From("daily-standup");

        var first = PulseIdempotencyKey.From(Org, Position, schedule, window);
        var second = PulseIdempotencyKey.From(Org, Position, schedule, window);

        Assert.Equal(first, second);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(
            "acme/bug-triage/daily-standup/2026-07-04T09:00:00.0000000Z/2026-07-04T10:00:00.0000000Z",
            first.Value);
    }

    [Fact]
    public void IdempotencyKey_differs_when_any_component_differs()
    {
        var window = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero));
        var otherWindow = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 5, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 5, 10, 0, 0, TimeSpan.Zero));
        var baseline = PulseIdempotencyKey.From(Org, Position, ScheduleId.From("s1"), window);

        Assert.NotEqual(baseline, PulseIdempotencyKey.From(OrganizationId.From("other"), Position, ScheduleId.From("s1"), window));
        Assert.NotEqual(baseline, PulseIdempotencyKey.From(Org, PositionId.From("other"), ScheduleId.From("s1"), window));
        Assert.NotEqual(baseline, PulseIdempotencyKey.From(Org, Position, ScheduleId.From("s2"), window));
        Assert.NotEqual(baseline, PulseIdempotencyKey.From(Org, Position, ScheduleId.From("s1"), otherWindow));
    }

    [Fact]
    public void IdempotencyKey_rejects_components_with_reserved_separator()
    {
        var window = TemporalWindow.From(
            new DateTimeOffset(2026, 7, 4, 9, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2026, 7, 4, 10, 0, 0, TimeSpan.Zero));

        // Organization ids may structurally contain '/', but the key composition must reject it.
        Assert.Throws<ArgumentException>(() =>
            PulseIdempotencyKey.From(OrganizationId.From("a/b"), Position, ScheduleId.From("s"), window));
    }
}
