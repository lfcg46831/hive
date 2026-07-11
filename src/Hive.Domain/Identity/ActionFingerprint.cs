namespace Hive.Domain.Identity;

public sealed record ActionFingerprint
{
    private ActionFingerprint(string value) => Value = value;

    public string Value { get; }

    public static ActionFingerprint From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
