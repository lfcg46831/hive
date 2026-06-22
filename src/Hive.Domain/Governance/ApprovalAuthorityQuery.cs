using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// The context an <see cref="IApprovalAuthority"/> implementation needs to resolve the authorized
/// approver of an <see cref="ApprovalRequest"/> (US-F0-04-T07a): the organization scope, the
/// <see cref="ApprovalPolicyRef"/> selector, the requesting position, the approver proposed on the
/// request and the action (approval type) being requested.
/// </summary>
/// <remarks>
/// The query carries every fact the bible lists as an input to resolution. In F0 the materialized
/// resolver resolves the authorized approver from the policy and action declared in the authority
/// configuration; <see cref="Requester"/> and <see cref="ProposedApprover"/> are supplied as
/// context for audit and for alternative implementations (for example future requester-relative
/// policies) without changing the seam.
/// </remarks>
public sealed record ApprovalAuthorityQuery
{
    /// <summary>
    /// Creates a resolution query for <paramref name="policy"/> within
    /// <paramref name="organizationId"/>.
    /// </summary>
    public ApprovalAuthorityQuery(
        OrganizationId organizationId,
        ApprovalPolicyRef policy,
        PositionId requester,
        EndpointRef proposedApprover,
        string action)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(policy);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(proposedApprover);

        OrganizationId = organizationId;
        Policy = policy;
        Requester = requester;
        ProposedApprover = proposedApprover;
        Action = IdentityValue.RequireStructural(action, nameof(action));
    }

    /// <summary>The organization the request belongs to; resolution is scoped to it.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>The logical approval policy selector carried by the request.</summary>
    public ApprovalPolicyRef Policy { get; }

    /// <summary>The position that issued the <see cref="ApprovalRequest"/>.</summary>
    public PositionId Requester { get; }

    /// <summary>The approver endpoint proposed on the request's destination.</summary>
    public EndpointRef ProposedApprover { get; }

    /// <summary>The action (approval type) the request asks to authorize.</summary>
    public string Action { get; }
}
