namespace Hive.Domain.Identity;

public sealed record UnitId
{
    private UnitId(string value) => Value = value;

    public string Value { get; }

    public static UnitId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
