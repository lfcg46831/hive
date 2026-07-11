namespace Hive.Domain.Identity;

public sealed record RetainedActionId
{
    private RetainedActionId(Guid value) => Value = value;

    public Guid Value { get; }

    public static RetainedActionId New() => From(Guid.NewGuid());

    public static RetainedActionId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
