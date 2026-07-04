using Hive.Domain.Identity;

namespace Hive.Domain.Scheduling;

/// <summary>
/// The deterministic idempotency key of a scheduled <c>Pulse</c> (§7 "Idempotência"): a firing is
/// uniquely identified by the tuple <c>(organization, position, schedule, temporal window)</c>. Because
/// the key is a pure function of that tuple, a redelivery after a cluster failover produces the exact
/// same key and therefore cannot duplicate completed work (US-F0-09 acceptance criteria).
/// </summary>
/// <remarks>
/// The canonical form is <c>{organization}/{position}/{scheduleId}/{startUtc}/{endUtc}</c> with '/' as
/// the single reserved separator; every segment forbids '/' so the composition round-trips
/// unambiguously. Instants are normalized to UTC (see <see cref="TemporalWindow.ToCanonicalString"/>).
/// Deriving deterministic <c>MessageId</c>/<c>ThreadId</c> from this key belongs to US-F0-09-T06.
/// </remarks>
public sealed record PulseIdempotencyKey
{
    /// <summary>The single reserved separator between key segments.</summary>
    public const char Separator = '/';

    private PulseIdempotencyKey(
        OrganizationId organization,
        PositionId position,
        ScheduleId schedule,
        TemporalWindow window,
        string value)
    {
        Organization = organization;
        Position = position;
        Schedule = schedule;
        Window = window;
        Value = value;
    }

    public OrganizationId Organization { get; }

    public PositionId Position { get; }

    public ScheduleId Schedule { get; }

    public TemporalWindow Window { get; }

    /// <summary>The canonical textual form of the key.</summary>
    public string Value { get; }

    /// <summary>
    /// Composes the deterministic key from its components. The organization and position must not
    /// contain the reserved separator (the schedule id already forbids it by construction), otherwise
    /// the composite could not be attributed unambiguously.
    /// </summary>
    public static PulseIdempotencyKey From(
        OrganizationId organization,
        PositionId position,
        ScheduleId schedule,
        TemporalWindow window)
    {
        ArgumentNullException.ThrowIfNull(organization);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(schedule);
        ArgumentNullException.ThrowIfNull(window);

        IdentityValue.RequireWithout(organization.Value, Separator, nameof(organization));
        IdentityValue.RequireWithout(position.Value, Separator, nameof(position));

        var value = string.Join(
            Separator,
            organization.Value,
            position.Value,
            schedule.Value,
            window.ToCanonicalString());

        return new PulseIdempotencyKey(organization, position, schedule, window, value);
    }

    public override string ToString() => Value;
}
