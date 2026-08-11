namespace Hive.Domain.Identity;

/// <summary>
/// Opaque reference to an operational occupant-channel binding. The referenced endpoint is not
/// part of this identity and must be resolved only by the channel adapter at delivery time.
/// </summary>
public sealed record OccupantChannelBindingId
{
    private OccupantChannelBindingId(Guid value) => Value = value;

    public Guid Value { get; }

    public static OccupantChannelBindingId New() => From(Guid.NewGuid());

    public static OccupantChannelBindingId From(Guid value) =>
        new(IdentityValue.RequireMessage(value, nameof(value)));

    public override string ToString() => Value.ToString("D");
}
