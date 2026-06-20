namespace Hive.Domain.Identity;

public sealed record MessageId
{
    private MessageId(Guid value) => Value = value;

    public Guid Value { get; }

    public static MessageId New() => From(Guid.NewGuid());

    public static MessageId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
