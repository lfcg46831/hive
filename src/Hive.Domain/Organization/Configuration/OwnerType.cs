namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The kind of <c>OrganizationOwner</c> declared in <c>organization.owner.type</c> (§4.8): a single
/// <see cref="Human"/> or a <see cref="Group"/>. The owner sits outside the operational command
/// chain — it receives top-level escalations and operates the kill switch.
/// </summary>
public enum OwnerType
{
    /// <summary>A single human owner.</summary>
    Human,

    /// <summary>A group acting as owner.</summary>
    Group,
}
