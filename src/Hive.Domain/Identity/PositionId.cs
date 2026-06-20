namespace Hive.Domain.Identity;

public sealed record PositionId
{
    private PositionId(string value) => Value = value;

    public string Value { get; }

    public static PositionId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
