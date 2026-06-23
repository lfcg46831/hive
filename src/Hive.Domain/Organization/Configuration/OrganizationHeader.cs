using Hive.Domain.Identity;

namespace Hive.Domain.Organization.Configuration;

/// <summary>
/// The header block of an organization document (§4.8 <c>organization</c>): the organization
/// <see cref="Id"/>, optional human-readable <see cref="Name"/>, the <see cref="RootUnit"/> the tree
/// is rooted at, and the <see cref="Owner"/>. The model captures the declared shape only; that
/// <see cref="RootUnit"/> exists in <c>units</c> with <c>parent: null</c> is checked later
/// (US-F0-05-T06/T07).
/// </summary>
public sealed record OrganizationHeader
{
    /// <summary>Creates the header for organization <paramref name="id"/> rooted at <paramref name="rootUnit"/>.</summary>
    public OrganizationHeader(OrganizationId id, UnitId rootUnit, OwnerConfiguration owner, string? name = null)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(rootUnit);
        ArgumentNullException.ThrowIfNull(owner);

        Id = id;
        RootUnit = rootUnit;
        Owner = owner;
        Name = name;
    }

    /// <summary>The unique, stable organization identifier scoping every reference in the document.</summary>
    public OrganizationId Id { get; }

    /// <summary>The optional human-readable label of the organization.</summary>
    public string? Name { get; }

    /// <summary>The unit the organizational tree is rooted at.</summary>
    public UnitId RootUnit { get; }

    /// <summary>The configured owner outside the operational chain.</summary>
    public OwnerConfiguration Owner { get; }
}
