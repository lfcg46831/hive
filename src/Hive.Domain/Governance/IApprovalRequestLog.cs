using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// Read-only correlation seam over the original <see cref="ApprovalRequest"/> records of a single
/// organization, used by the decision validator (US-F0-04-T07b) to correlate an incoming
/// <see cref="ApprovalDecision"/> with the request it answers.
/// </summary>
/// <remarks>
/// <para>
/// This is a pure query seam, parallel to <c>IOrganizationRelations</c> and
/// <see cref="IApprovalAuthority"/>: implementations never accept or reject messages and never
/// re-resolve the approver. They only return what was recorded when the original request was
/// accepted, so the decision is validated against the approver, window and lifecycle state that were
/// actually in force.
/// </para>
/// <para>
/// Every query is scoped to a single <see cref="OrganizationId"/>. A request that is not recorded for
/// the given organization and identifier yields <see langword="null"/>; this <see langword="null"/>
/// is the existence probe the validator maps to a confirmed correlation failure, distinct from
/// cancellation or technical unavailability which remain exceptions subject to retry.
/// </para>
/// </remarks>
public interface IApprovalRequestLog
{
    /// <summary>
    /// Finds the recorded original request <paramref name="requestId"/> within
    /// <paramref name="organizationId"/>.
    /// </summary>
    /// <returns>
    /// The <see cref="ApprovalRequestRecord"/>, or <see langword="null"/> when no such request is
    /// recorded for the organization.
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="organizationId"/> or <paramref name="requestId"/> is <see langword="null"/>.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// <paramref name="cancellationToken"/> was cancelled.
    /// </exception>
    ValueTask<ApprovalRequestRecord?> FindRequestAsync(
        OrganizationId organizationId,
        MessageId requestId,
        CancellationToken cancellationToken = default);
}
