using Hive.Domain.Identity;

namespace Hive.Domain.Governance;

/// <summary>
/// Thrown by <see cref="IApprovalAuthority"/> implementations when a resolution targets an
/// organization that does not exist in the materialized authority registry.
/// </summary>
/// <remarks>
/// This exception signals a structural lookup failure (an unknown organization scope), not a valid
/// "no approver" answer. Semantic outcomes — a policy that is not declared or that does not
/// authorize the requested action — are returned as <see cref="ApproverResolution"/> instead, so a
/// caller can distinguish a confirmed absence (mapped to <c>InvalidRoute</c> by the routing
/// validator) from a technical/registry failure that must remain an exception.
/// </remarks>
public sealed class ApprovalAuthorityNotFoundException : Exception
{
    private ApprovalAuthorityNotFoundException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Creates an exception for an organization that is absent from the authority registry.
    /// </summary>
    public static ApprovalAuthorityNotFoundException ForOrganization(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        return new ApprovalAuthorityNotFoundException(
            $"Organization '{organizationId.Value}' was not found in the approval authority registry.");
    }
}
