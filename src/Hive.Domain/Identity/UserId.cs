namespace Hive.Domain.Identity;

/// <summary>Stable, internal identity of a user. It carries no external identity attributes.</summary>
public sealed record UserId
{
    private UserId(Guid value) => Value = value;

    public Guid Value { get; }

    public static UserId New() => From(Guid.NewGuid());

    public static UserId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
