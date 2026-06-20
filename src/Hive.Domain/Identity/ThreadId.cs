namespace Hive.Domain.Identity;

public sealed record ThreadId
{
    private ThreadId(Guid value) => Value = value;

    public Guid Value { get; }

    public static ThreadId New() => From(Guid.NewGuid());

    public static ThreadId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
