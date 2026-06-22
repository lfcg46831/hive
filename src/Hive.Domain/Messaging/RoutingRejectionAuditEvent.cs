using System.Collections.Immutable;
using Hive.Domain.Governance;
using Hive.Domain.Identity;

namespace Hive.Domain.Messaging;

/// <summary>
/// The auditable event recorded when an incoming <see cref="OrgMessage"/> is rejected by routing
/// validation at the inbox entry point (US-F0-04-T10). It projects a <see cref="RoutingRejection"/>
/// into the structured record the authorized audit trail keeps: the coarse-grained
/// <see cref="Reasons"/>, the fine-grained <see cref="Errors"/>, and — drawn from the rejection's
/// <see cref="RoutingValidationContext"/> — the route and policy actually presented by the message
/// (received) alongside the route and policy routing resolved/required (expected).
/// </summary>
/// <remarks>
/// <para>
/// The event is a pure-domain projection: it adds no behaviour beyond deriving the distinct
/// <see cref="Reasons"/> from the audit errors and stamping <see cref="OccurredAt"/> supplied by the
/// caller. Emitting or persisting it (structured log, event store) belongs to the host: the sharded,
/// persistent <see cref="OrgMessage"/> entry point (US-F0-06) records this event, and the persisted
/// event modelling lives in US-F0-06-T03. Keeping the projection in the domain lets routing, metrics
/// and audit share one stable contract.
/// </para>
/// <para>
/// <strong>Reason.</strong> <see cref="Reasons"/> is the distinct, deterministically ordered set of
/// coarse-grained <see cref="RejectionReason"/> drawn from the rejection's detailed audit result, and
/// <see cref="Errors"/> keeps every fine-grained <see cref="ValidationError"/> (stable
/// <see cref="ValidationError.Code"/> and dotted <see cref="ValidationError.Path"/>). Per §9.8 the full
/// list of reasons is preserved; it is never collapsed to a single "first" reason.
/// </para>
/// <para>
/// <strong>Received chain/policy.</strong> <see cref="Sender"/> and <see cref="Recipient"/> are the
/// route the message presented (its <c>From</c>/<c>To</c>); <see cref="ReceivedPolicy"/> is the
/// governance policy selector it referenced, or <see langword="null"/> for vertical routing.
/// </para>
/// <para>
/// <strong>Expected chain/policy.</strong> For governance routing, <see cref="ExpectedApprover"/> is
/// the authorized approver routing resolved and <see cref="ExpectedPolicyVersion"/> is the applied
/// policy version/hash; both are <see langword="null"/> for vertical routing, where the expected
/// relation is instead carried by the constraint <see cref="Errors"/> (for example
/// <c>direct-subordinate-required</c> at <c>to.positionId</c>). These identifiers stay in the
/// authorized audit trail and are never surfaced to the sender, in line with §9.8.
/// </para>
/// </remarks>
public sealed record RoutingRejectionAuditEvent
{
    private RoutingRejectionAuditEvent(
        DateTimeOffset occurredAt,
        RoutingValidationContext context,
        ImmutableArray<ValidationError> errors,
        ImmutableArray<RejectionReason> reasons)
    {
        OccurredAt = occurredAt;
        Context = context;
        Errors = errors;
        Reasons = reasons;
    }

    /// <summary>When the rejection occurred, as supplied by the recording entry point.</summary>
    public DateTimeOffset OccurredAt { get; }

    /// <summary>The auditable context of the rejected message.</summary>
    public RoutingValidationContext Context { get; }

    /// <summary>The identifier of the original message that was rejected.</summary>
    public MessageId MessageId => Context.MessageId;

    /// <summary>The organization the rejected message belongs to.</summary>
    public OrganizationId OrganizationId => Context.OrganizationId;

    /// <summary>The conversation thread the rejected message belongs to.</summary>
    public ThreadId Thread => Context.Thread;

    /// <summary>
    /// The distinct, deterministically ordered coarse-grained reasons for the rejection. Never empty.
    /// </summary>
    public IReadOnlyList<RejectionReason> Reasons { get; }

    /// <summary>
    /// The detailed, fine-grained audit errors (the expected constraint per field) backing the
    /// rejection. These are the audit errors, not the sanitized public result.
    /// </summary>
    public IReadOnlyList<ValidationError> Errors { get; }

    /// <summary>The originating endpoint of the rejected message (the received chain's source).</summary>
    public EndpointRef Sender => Context.Sender;

    /// <summary>The destination endpoint of the rejected message (the received chain's target).</summary>
    public EndpointRef Recipient => Context.Recipient;

    /// <summary>
    /// The governance policy the message referenced (received), or <see langword="null"/> for vertical
    /// routing.
    /// </summary>
    public ApprovalPolicyRef? ReceivedPolicy => Context.Policy;

    /// <summary>
    /// The authorized approver routing resolved for the governance message (expected), or
    /// <see langword="null"/> for vertical routing or when no approver could be resolved.
    /// </summary>
    public EndpointRef? ExpectedApprover => Context.ResolvedApprover;

    /// <summary>
    /// The applied policy version/hash routing interpreted (expected), or <see langword="null"/> for
    /// vertical routing or when no policy was found.
    /// </summary>
    public ApprovalPolicyVersion? ExpectedPolicyVersion => Context.AppliedVersion;

    /// <summary>
    /// Projects the <paramref name="rejection"/> into an audit event stamped with
    /// <paramref name="occurredAt"/>, deriving the distinct reason set from its detailed audit result.
    /// </summary>
    /// <exception cref="ArgumentNullException"><paramref name="rejection"/> is <see langword="null"/>.</exception>
    public static RoutingRejectionAuditEvent FromRejection(
        RoutingRejection rejection,
        DateTimeOffset occurredAt)
    {
        ArgumentNullException.ThrowIfNull(rejection);

        var errors = rejection.AuditResult.Errors.ToImmutableArray();
        var reasons = errors
            .Select(error => error.Reason)
            .Distinct()
            .OrderBy(reason => reason)
            .ToImmutableArray();

        return new RoutingRejectionAuditEvent(occurredAt, rejection.Context, errors, reasons);
    }
}
