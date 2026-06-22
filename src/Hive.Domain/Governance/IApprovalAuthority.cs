using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// Read-only resolution contract over the materialized authority policy of a single organization.
/// It resolves the authorized approver of an <see cref="ApprovalRequest"/> from an
/// <see cref="ApprovalPolicyRef"/>, the requester, the proposed destination, the action and the
/// organizational context (US-F0-04-T07a), returning the concrete approver endpoint together with
/// the version/hash of the configuration that was applied.
/// </summary>
/// <remarks>
/// <para>
/// This is a pure query seam, parallel to <c>IOrganizationRelations</c>: implementations never
/// mutate the registry and never accept or reject messages. Resolving only produces the
/// authoritative approver and the applied policy version; comparing the proposed destination with
/// the resolved approver, correlating the original request and rejecting unauthorized, duplicate or
/// expired decisions belong to US-F0-04-T07b/T07c.
/// </para>
/// <para>
/// Every query is scoped to a single <see cref="OrganizationId"/>. An unknown organization is a
/// structural lookup failure surfaced through <see cref="ApprovalAuthorityNotFoundException"/>,
/// distinct from the semantic outcomes of <see cref="ApproverResolution"/>: a policy that is not
/// declared yields <see cref="ApproverResolutionStatus.PolicyNotFound"/> and a policy that does not
/// authorize the action yields <see cref="ApproverResolutionStatus.ActionNotAuthorized"/>, so the
/// routing validator can map confirmed absences to <c>InvalidRoute</c> while technical failures stay
/// exceptions.
/// </para>
/// </remarks>
public interface IApprovalAuthority
{
    /// <summary>
    /// Resolves the authorized approver for <paramref name="query"/>.
    /// </summary>
    /// <returns>
    /// A <see cref="ApproverResolution"/> carrying the concrete approver endpoint and applied policy
    /// version when resolved, or a structured reason when the policy is absent or does not authorize
    /// the action.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="query"/> is <see langword="null"/>.</exception>
    /// <exception cref="ApprovalAuthorityNotFoundException">
    /// The organization is not present in the materialized authority registry.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    ValueTask<ApproverResolution> ResolveApproverAsync(
        ApprovalAuthorityQuery query,
        CancellationToken cancellationToken = default);
}
