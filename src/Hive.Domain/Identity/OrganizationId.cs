namespace Hive.Domain.Identity;

public sealed record OrganizationId
{
    private OrganizationId(string value) => Value = value;

    public string Value { get; }

    public static OrganizationId From(string value) =>
        new(IdentityValue.RequireStructural(value, nameof(value)));

    public override string ToString() => Value;
}
