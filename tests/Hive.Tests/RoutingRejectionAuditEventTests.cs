using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RoutingRejectionAuditEventTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly ApprovalPolicyRef Policy = ApprovalPolicyRef.From("requires-human-approval");
    private static readonly ApprovalPolicyVersion Version =
        ApprovalPolicyVersion.Create("v3", "sha256:abc123");
    private static readonly DateTimeOffset OccurredAt =
        new(2026, 6, 22, 12, 0, 0, TimeSpan.Zero);

    private static readonly PositionEndpointRef Engineer = Position("engineer");
    private static readonly PositionEndpointRef DeliveryLead = Position("delivery-lead");
    private static readonly PositionEndpointRef SecurityOfficer = Position("security-officer");

    [Fact]
    public void FromRejection_stamps_occurred_at_and_carries_message_identity()
    {
        var id = MessageId.New();
        var thread = ThreadId.New();
        var rejection = VerticalRejection(id, thread);

        var audit = RoutingRejectionAuditEvent.FromRejection(rejection, OccurredAt);

        Assert.Equal(OccurredAt, audit.OccurredAt);
        Assert.Equal(id, audit.MessageId);
        Assert.Equal(Org, audit.OrganizationId);
        Assert.Equal(thread, audit.Thread);
        Assert.Same(rejection.Context, audit.Context);
    }

    [Fact]
    public void FromRejection_derives_distinct_reasons_in_deterministic_order()
    {
        var rejection = GovernanceRejection(
            ApprovalValidationCatalog.AuthorizedApproverRequired(), // InvalidRoute
            ApprovalValidationCatalog.UnauthorizedApprover(),       // Unauthorized
            ApprovalValidationCatalog.ApprovalRequestExpired());    // Expired

        var audit = RoutingRejectionAuditEvent.FromRejection(rejection, OccurredAt);

        Assert.Equal(
            [RejectionReason.InvalidRoute, RejectionReason.Unauthorized, RejectionReason.Expired],
            audit.Reasons);
    }

    [Fact]
    public void FromRejection_collapses_repeated_reasons_but_keeps_every_detailed_error()
    {
        // Two distinct vertical errors, both InvalidRoute.
        var rejection = VerticalRejection(
            MessageId.New(),
            ThreadId.New(),
            RoutingValidationCatalog.EndpointNotAllowed("from"),
            RoutingValidationCatalog.DirectSubordinateRequired());

        var audit = RoutingRejectionAuditEvent.FromRejection(rejection, OccurredAt);

        Assert.Equal([RejectionReason.InvalidRoute], audit.Reasons);
        // The detailed audit errors are preserved verbatim from the audit result, not sanitized.
        Assert.Equal(rejection.AuditResult.Errors, audit.Errors);
        Assert.Equal(2, audit.Errors.Count);
    }

    [Fact]
    public void Vertical_rejection_exposes_received_chain_without_governance_facts()
    {
        var rejection = VerticalRejection(MessageId.New(), ThreadId.New());

        var audit = RoutingRejectionAuditEvent.FromRejection(rejection, OccurredAt);

        // Received chain.
        Assert.Equal(rejection.Context.Sender, audit.Sender);
        Assert.Equal(rejection.Context.Recipient, audit.Recipient);
        // No governance policy/chain was resolved for vertical routing.
        Assert.Null(audit.ReceivedPolicy);
        Assert.Null(audit.ExpectedApprover);
        Assert.Null(audit.ExpectedPolicyVersion);
    }

    [Fact]
    public void Governance_rejection_exposes_received_and_expected_chain_and_policy()
    {
        var context = new RoutingValidationContext(
            MessageId.New(),
            Org,
            SecurityOfficer, // received sender (unauthorized)
            DeliveryLead,    // received recipient
            ThreadId.New(),
            Policy,          // received policy
            Version,         // expected policy version
            DeliveryLead);   // expected approver
        var rejection = RoutingRejection.Create(
            context,
            ValidationResult.Create([ApprovalValidationCatalog.UnauthorizedApprover()]));

        var audit = RoutingRejectionAuditEvent.FromRejection(rejection, OccurredAt);

        // Received chain/policy.
        Assert.Equal(SecurityOfficer, audit.Sender);
        Assert.Equal(DeliveryLead, audit.Recipient);
        Assert.Equal(Policy, audit.ReceivedPolicy);
        // Expected chain/policy.
        Assert.Equal(DeliveryLead, audit.ExpectedApprover);
        Assert.Equal(Version, audit.ExpectedPolicyVersion);
    }

    [Fact]
    public void Reasons_is_never_empty()
    {
        var audit = RoutingRejectionAuditEvent.FromRejection(
            VerticalRejection(MessageId.New(), ThreadId.New()),
            OccurredAt);

        Assert.NotEmpty(audit.Reasons);
    }

    [Fact]
    public void Missing_rejection_is_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => RoutingRejectionAuditEvent.FromRejection(null!, OccurredAt));
    }

    // ---- helpers ----

    private static RoutingRejection VerticalRejection(
        MessageId id,
        ThreadId thread,
        params ValidationError[] errors)
    {
        var context = new RoutingValidationContext(id, Org, Engineer, DeliveryLead, thread);
        var audit = errors.Length == 0
            ? ValidationResult.Create([RoutingValidationCatalog.DirectSubordinateRequired()])
            : ValidationResult.Create(errors);
        return RoutingRejection.Create(context, audit);
    }

    private static RoutingRejection GovernanceRejection(params ValidationError[] errors)
    {
        var context = new RoutingValidationContext(
            MessageId.New(),
            Org,
            SecurityOfficer,
            DeliveryLead,
            ThreadId.New(),
            Policy,
            Version,
            DeliveryLead);
        return RoutingRejection.Create(context, ValidationResult.Create(errors));
    }

    private static PositionEndpointRef Position(string id) => new(PositionId.From(id));
}
