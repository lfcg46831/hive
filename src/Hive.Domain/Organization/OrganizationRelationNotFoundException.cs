using Hive.Domain.Identity;

namespace Hive.Domain.Organization;

/// <summary>
/// Thrown by <see cref="IOrganizationRelations"/> implementations when a query targets an
/// organization or position that does not exist in the materialized registry.
/// </summary>
/// <remarks>
/// This exception signals a structural lookup failure (unknown organization or position),
/// not a valid "no relation" answer. Queries distinguish these cases deliberately: a
/// <see langword="null"/> direct superior means the position is the root unit leadership and
/// therefore has no organizational superior, whereas an unknown position is an error and is
/// surfaced through this exception. Callers that want to probe existence without handling an
/// exception should use <see cref="IOrganizationRelations.GetUnitOfPositionAsync"/>, which
/// returns <see langword="null"/> for unknown positions.
/// </remarks>
public sealed class OrganizationRelationNotFoundException : Exception
{
    private OrganizationRelationNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception for an organization that is absent from the registry.
    /// </summary>
    public static OrganizationRelationNotFoundException ForOrganization(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        return new OrganizationRelationNotFoundException(
            $"Organization '{organizationId.Value}' was not found in the registry.");
    }

    /// <summary>
    /// Creates an exception for a position that is absent from the given organization.
    /// </summary>
    public static OrganizationRelationNotFoundException ForPosition(
        OrganizationId organizationId,
        PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);

        return new OrganizationRelationNotFoundException(
            $"Position '{positionId.Value}' was not found in organization '{organizationId.Value}'.");
    }
}
