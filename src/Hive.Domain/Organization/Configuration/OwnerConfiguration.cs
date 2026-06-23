namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The configured <c>OrganizationOwner</c> (§4.8 <c>organization.owner</c>): the owner
/// <see cref="Type"/> and the stable identifier (<see cref="Ref"/>) of the human or group outside
/// the operational chain. This is the loaded shape only; whether the reference resolves is not
/// checked here.
/// </summary>
public sealed record OwnerConfiguration
{
    /// <summary>Creates an owner of <paramref name="type"/> identified by <paramref name="reference"/>.</summary>
    public OwnerConfiguration(OwnerType type, string reference)
    {
        ArgumentNullException.ThrowIfNull(reference);

        Type = type;
        Ref = reference;
    }

    /// <summary>Whether the owner is a single human or a group.</summary>
    public OwnerType Type { get; }

    /// <summary>The stable identifier of the human/group (for example an e-mail or group handle).</summary>
    public string Ref { get; }
}
