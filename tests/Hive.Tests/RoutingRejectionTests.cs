using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class RoutingRejectionTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");

    private static RoutingValidationContext Context() =>
        new(
            MessageId.New(),
            Org,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("engineer")),
            ThreadId.New());

    [Fact]
    public void Public_result_normalizes_detail_to_coarse_reason_and_generic_path()
    {
        var audit = ValidationResult.Create(
        [
            RoutingValidationCatalog.PositionNotFound("from.positionId"),
            RoutingValidationCatalog.DirectSubordinateRequired(),
        ]);

        var rejection = RoutingRejection.Create(Context(), audit);

        // Both detailed errors share RejectionReason.InvalidRoute, so the public surface collapses
        // them to a single generic entry that does not enumerate positions or routes.
        Assert.Equal(
            [new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute)],
            rejection.PublicResult.Errors);
    }

    [Fact]
    public void Public_result_keeps_one_entry_per_distinct_reason()
    {
        var audit = ValidationResult.Create(
        [
            ApprovalValidationCatalog.AuthorizedApproverRequired(), // InvalidRoute
            ApprovalValidationCatalog.UnauthorizedApprover(),       // Unauthorized
            ApprovalValidationCatalog.ApprovalDecisionExpired(),    // Expired
        ]);

        var rejection = RoutingRejection.Create(Context(), audit);

        Assert.Equal(
            [
                new ValidationError("expired", "$", RejectionReason.Expired),
                new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute),
                new ValidationError("unauthorized", "$", RejectionReason.Unauthorized),
            ],
            rejection.PublicResult.Errors);
    }

    [Fact]
    public void Audit_result_preserves_full_detail()
    {
        var audit = ValidationResult.Create(
        [
            ApprovalValidationCatalog.UnauthorizedApprover(),
        ]);

        var rejection = RoutingRejection.Create(Context(), audit);

        Assert.Same(audit, rejection.AuditResult);
        Assert.Equal(
            [new ValidationError("unauthorized-approver", "from", RejectionReason.Unauthorized)],
            rejection.AuditResult.Errors);
    }

    [Fact]
    public void Context_is_preserved_for_audit()
    {
        var policy = ApprovalPolicyRef.From("budget-approval");
        var version = ApprovalPolicyVersion.Create("v3", "deadbeef");
        var approver = new OrganizationOwnerEndpointRef();
        var context = Context().WithGovernance(policy, version, approver);

        var rejection = RoutingRejection.Create(
            context,
            ValidationResult.Create([ApprovalValidationCatalog.UnauthorizedApprover()]));

        Assert.Same(context, rejection.Context);
        Assert.Equal(policy, rejection.Context.Policy);
        Assert.Equal(version, rejection.Context.AppliedVersion);
        Assert.Equal(approver, rejection.Context.ResolvedApprover);
    }

    [Fact]
    public void Valid_result_is_not_a_rejection()
    {
        Assert.Throws<ArgumentException>(
            () => RoutingRejection.Create(Context(), ValidationResult.Valid));
    }

    [Fact]
    public void Null_arguments_are_internal_api_misuse()
    {
        var audit = ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);

        Assert.Throws<ArgumentNullException>(() => RoutingRejection.Create(null!, audit));
        Assert.Throws<ArgumentNullException>(() => RoutingRejection.Create(Context(), null!));
    }
}
