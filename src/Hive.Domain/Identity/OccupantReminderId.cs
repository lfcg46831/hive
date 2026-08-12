namespace Hive.Domain.Identity;

/// <summary>Stable identity of one scheduled occupant-channel reminder.</summary>
public sealed record OccupantReminderId
{
    private OccupantReminderId(Guid value) => Value = value;

    public Guid Value { get; }

    public static OccupantReminderId New() => From(Guid.NewGuid());

    public static OccupantReminderId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
