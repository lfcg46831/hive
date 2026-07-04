namespace Hive.Domain.Identity;

/// <summary>
/// The identity of a declared schedule within a position (§6.2 <c>occupant.schedule[].id</c>).
/// It is a structural identity — like <see cref="PositionId"/> — and is one of the segments of the
/// deterministic Pulse idempotency key, so it must not contain the reserved '/' separator used to
/// compose that key.
/// </summary>
public sealed record ScheduleId
{
    private ScheduleId(string value) => Value = value;

    public string Value { get; }

    public static ScheduleId From(string value)
    {
        var structural = IdentityValue.RequireStructural(value, nameof(value));
        return new ScheduleId(IdentityValue.RequireWithout(structural, '/', nameof(value)));
    }

    public override string ToString() => Value;
}
