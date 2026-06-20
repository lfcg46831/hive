namespace Hive.Domain.Identity;

public sealed record DirectiveId
{
    private DirectiveId(Guid value) => Value = value;

    public Guid Value { get; }

    public static DirectiveId New() => From(Guid.NewGuid());

    public static DirectiveId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
