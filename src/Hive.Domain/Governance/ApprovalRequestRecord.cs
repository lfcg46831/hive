using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Governance;

/// <summary>
/// The recorded outcome of accepting an <see cref="ApprovalRequest"/>: the authoritative approver
/// resolved at acceptance time (US-F0-04-T07a), the requester it must answer back to, the applied
/// policy version and the lifecycle facts the decision validator needs to correlate an incoming
/// <see cref="ApprovalDecision"/> (US-F0-04-T07b).
/// </summary>
/// <remarks>
/// The record persists what was interpreted when the request was accepted, so a later
/// <see cref="ApprovalDecision"/> is validated against the approver and window that were actually in
/// force, independently of subsequent edits to the authority configuration. The decision validator
/// never re-resolves the approver: it compares the decision against <see cref="ResolvedApprover"/>
/// recorded here.
/// </remarks>
public sealed record ApprovalRequestRecord
{
    /// <summary>
    /// Creates a record for the accepted request <paramref name="requestId"/>.
    /// </summary>
    public ApprovalRequestRecord(
        MessageId requestId,
        OrganizationId organizationId,
        PositionId requester,
        EndpointRef resolvedApprover,
        ApprovalPolicyVersion appliedVersion,
        ThreadId thread,
        DateTimeOffset? deadline,
        MessageState state)
    {
        ArgumentNullException.ThrowIfNull(requestId);
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(requester);
        ArgumentNullException.ThrowIfNull(resolvedApprover);
        ArgumentNullException.ThrowIfNull(appliedVersion);
        ArgumentNullException.ThrowIfNull(thread);

        RequestId = requestId;
        OrganizationId = organizationId;
        Requester = requester;
        ResolvedApprover = resolvedApprover;
        AppliedVersion = appliedVersion;
        Thread = thread;
        Deadline = deadline;
        State = MessageStateContract.RequireDefined(state, nameof(state));
    }

    /// <summary>The identifier of the original <see cref="ApprovalRequest"/>.</summary>
    public MessageId RequestId { get; }

    /// <summary>The organization the request belongs to.</summary>
    public OrganizationId OrganizationId { get; }

    /// <summary>The position that issued the original request and must receive the decision.</summary>
    public PositionId Requester { get; }

    /// <summary>The authorized approver endpoint resolved and recorded at acceptance time.</summary>
    public EndpointRef ResolvedApprover { get; }

    /// <summary>The version/hash of the authority configuration applied at acceptance time.</summary>
    public ApprovalPolicyVersion AppliedVersion { get; }

    /// <summary>The conversation thread the original request belongs to.</summary>
    public ThreadId Thread { get; }

    /// <summary>The approval window deadline of the original request, if any.</summary>
    public DateTimeOffset? Deadline { get; }

    /// <summary>The current lifecycle state of the original request.</summary>
    public MessageState State { get; }

    /// <summary>
    /// Whether the original request is still awaiting a decision (a non-terminal state). Terminal
    /// states (<see cref="MessageState.Completed"/>, <see cref="MessageState.Rejected"/> and
    /// <see cref="MessageState.Failed"/>) no longer accept a decision.
    /// </summary>
    public bool IsAwaitingDecision =>
        State is MessageState.Received or MessageState.Accepted or MessageState.Processing;
}
