using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

/// <summary>
/// The auditable context of a routing validation outcome (US-F0-04-T08): the identity of the
/// original message together with the routing facts that audit needs to correlate a rejection — the
/// sender, the recipient, the conversation thread and, for governance routing, the
/// <see cref="ApprovalPolicyRef"/>, the applied policy version/hash and the resolved approver.
/// </summary>
/// <remarks>
/// <para>
/// This context travels alongside the detailed <see cref="ValidationResult"/> inside a
/// <see cref="RoutingRejection"/>. It carries the sensitive identifiers (positions, policies,
/// resolved approver) that must stay in the authorized audit trail and must not be surfaced in the
/// public result returned to the sender, in line with §9.8 of the bible.
/// </para>
/// <para>
/// The governance fields (<see cref="Policy"/>, <see cref="AppliedVersion"/> and
/// <see cref="ResolvedApprover"/>) are <see langword="null"/> for vertical routing
/// (<see cref="Directive"/>, <see cref="Report"/>, <see cref="Escalation"/>) and populated for
/// governance routing (<see cref="ApprovalRequest"/>, <see cref="ApprovalDecision"/>). The applied
/// version and resolved approver come from the authority resolution (US-F0-04-T07a) or the recorded
/// <see cref="ApprovalRequestRecord"/> (US-F0-04-T07b), so audit persists exactly what was interpreted.
/// </para>
/// </remarks>
public sealed record RoutingValidationContext
{
    /// <summary>
    /// Creates a context for the original message <paramref name="messageId"/>. The governance fields
    /// are optional and left <see langword="null"/> for vertical routing.
    /// </summary>
    public RoutingValidationContext(
        MessageId messageId,
        OrganizationId organizationId,
        EndpointRef sender,
        EndpointRef recipient,
        ThreadId thread,
        ApprovalPolicyRef? policy = null,
        ApprovalPolicyVersion? appliedVersion = null,
        EndpointRef? resolvedApprover = null)
    {
        ArgumentNullException.ThrowIfNull(messageId);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(sender);
        ArgumentNullException.ThrowIfNull(recipient);
        ArgumentNullException.ThrowIfNull(thread);

        MessageId = messageId;
        OrganizationId = organizationId;
        Sender = sender;
        Recipient = recipient;
        Thread = thread;
        Policy = policy;
        AppliedVersion = appliedVersion;
        ResolvedApprover = resolvedApprover;
    }

    /// <summary>The identifier of the original message that was validated.</summary>
    public MessageId MessageId { get; }

    /// <summary>The organization the message belongs to.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>The originating endpoint of the message (its <c>From</c>).</summary>
    public EndpointRef Sender { get; }

    /// <summary>The destination endpoint of the message (its <c>To</c>).</summary>
    public EndpointRef Recipient { get; }

    /// <summary>The conversation thread the message belongs to.</summary>
    public ThreadId Thread { get; }

    /// <summary>The governance policy selector, or <see langword="null"/> for vertical routing.</summary>
    public ApprovalPolicyRef? Policy { get; }

    /// <summary>
    /// The version/hash of the authority configuration applied when the approver was resolved, or
    /// <see langword="null"/> for vertical routing or when no policy was found.
    /// </summary>
    public ApprovalPolicyVersion? AppliedVersion { get; }

    /// <summary>
    /// The authorized approver endpoint resolved for the governance message, or
    /// <see langword="null"/> for vertical routing or when no approver could be resolved.
    /// </summary>
    public EndpointRef? ResolvedApprover { get; }

    /// <summary>
    /// Creates the vertical-routing context from a validated message, capturing its identity,
    /// organization, endpoints and thread. Governance fields are left <see langword="null"/>.
    /// </summary>
    public static RoutingValidationContext ForMessage(OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new RoutingValidationContext(
            message.Id,
            message.OrganizationId,
            message.From,
            message.To,
            message.Thread);
    }

    /// <summary>
    /// Returns a copy of this context enriched with the governance routing facts: the resolved
    /// <paramref name="policy"/>, the <paramref name="appliedVersion"/> and the
    /// <paramref name="resolvedApprover"/>. Used by governance routing validation (US-F0-04-T07)
    /// once the approver has been resolved or correlated.
    /// </summary>
    public RoutingValidationContext WithGovernance(
        ApprovalPolicyRef policy,
        ApprovalPolicyVersion? appliedVersion,
        EndpointRef? resolvedApprover)
    {
        ArgumentNullException.ThrowIfNull(policy);

        return new RoutingValidationContext(
            MessageId,
            OrganizationId,
            Sender,
            Recipient,
            Thread,
            policy,
            appliedVersion,
            resolvedApprover);
    }
}
