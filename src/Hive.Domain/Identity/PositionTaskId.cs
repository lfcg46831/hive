namespace Hive.Domain.Identity;

/// <summary>
/// The identity of a unit of work tracked inside a position (a "tarefa" of the
/// <c>PositionActor</c>). It is opaque and position-scoped: the runtime addresses a task only in the
/// context of the position that owns it, so this identity carries no organization/position segment.
/// </summary>
public sealed record PositionTaskId
{
    private PositionTaskId(Guid value) => Value = value;

    public Guid Value { get; }

    public static PositionTaskId New() => From(Guid.NewGuid());

    public static PositionTaskId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
