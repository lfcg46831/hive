namespace Hive.Domain.Identity;

public sealed record OccupantId
{
    private OccupantId(string value) => Value = value;

    public string Value { get; }

    public static OccupantId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
