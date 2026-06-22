using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class ApprovalAuthorityContractTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly ApprovalPolicyRef Policy = ApprovalPolicyRef.From("requires-human-approval");
    private static readonly ApprovalPolicyVersion Version =
        ApprovalPolicyVersion.Create("v3", "sha256:abc123");

    [Fact]
    public void Applied_version_preserves_version_and_hash()
    {
        var version = ApprovalPolicyVersion.Create("v7", "sha256:deadbeef");

        Assert.Equal("v7", version.Version);
        Assert.Equal("sha256:deadbeef", version.Hash);
    }

    [Fact]
    public void Applied_versions_compare_by_value()
    {
        Assert.Equal(
            ApprovalPolicyVersion.Create("v3", "sha256:abc123"),
            ApprovalPolicyVersion.Create("v3", "sha256:abc123"));
        Assert.NotEqual(
            ApprovalPolicyVersion.Create("v3", "sha256:abc123"),
            ApprovalPolicyVersion.Create("v4", "sha256:abc123"));
    }

    [Theory]
    [InlineData(null, "sha256:abc")]
    [InlineData("", "sha256:abc")]
    [InlineData("   ", "sha256:abc")]
    [InlineData(" v3", "sha256:abc")]
    [InlineData("v3", null)]
    [InlineData("v3", "")]
    [InlineData("v3", "sha256:abc ")]
    public void Applied_version_rejects_non_structural_values(string? version, string? hash)
    {
        Assert.ThrowsAny<ArgumentException>(() => ApprovalPolicyVersion.Create(version!, hash!));
    }

    [Fact]
    public void Resolved_outcome_carries_approver_and_version()
    {
        var approver = new PositionEndpointRef(PositionId.From("delivery-lead"));

        var resolution = ApproverResolution.Resolved(Policy, approver, Version);

        Assert.True(resolution.IsResolved);
        Assert.Equal(ApproverResolutionStatus.Resolved, resolution.Status);
        Assert.Same(approver, resolution.ResolvedApprover);
        Assert.Same(Version, resolution.AppliedVersion);
    }

    [Fact]
    public void Policy_not_found_outcome_has_no_approver_or_version()
    {
        var resolution = ApproverResolution.PolicyNotFound(Policy);

        Assert.False(resolution.IsResolved);
        Assert.Equal(ApproverResolutionStatus.PolicyNotFound, resolution.Status);
        Assert.Null(resolution.ResolvedApprover);
        Assert.Null(resolution.AppliedVersion);
    }

    [Fact]
    public void Action_not_authorized_outcome_keeps_version_without_approver()
    {
        var resolution = ApproverResolution.ActionNotAuthorized(Policy, Version);

        Assert.False(resolution.IsResolved);
        Assert.Equal(ApproverResolutionStatus.ActionNotAuthorized, resolution.Status);
        Assert.Null(resolution.ResolvedApprover);
        Assert.Same(Version, resolution.AppliedVersion);
    }

    [Fact]
    public void Resolution_factories_reject_null_arguments()
    {
        var approver = new PositionEndpointRef(PositionId.From("delivery-lead"));

        Assert.Throws<ArgumentNullException>(() => ApproverResolution.Resolved(null!, approver, Version));
        Assert.Throws<ArgumentNullException>(() => ApproverResolution.Resolved(Policy, null!, Version));
        Assert.Throws<ArgumentNullException>(() => ApproverResolution.Resolved(Policy, approver, null!));
        Assert.Throws<ArgumentNullException>(() => ApproverResolution.PolicyNotFound(null!));
        Assert.Throws<ArgumentNullException>(() => ApproverResolution.ActionNotAuthorized(null!, Version));
        Assert.Throws<ArgumentNullException>(() => ApproverResolution.ActionNotAuthorized(Policy, null!));
    }

    [Fact]
    public void Query_rejects_null_arguments()
    {
        var approver = new PositionEndpointRef(PositionId.From("delivery-lead"));
        var requester = PositionId.From("engineer");

        Assert.Throws<ArgumentNullException>(
            () => new ApprovalAuthorityQuery(null!, Policy, requester, approver, "publicacao-final"));
        Assert.Throws<ArgumentNullException>(
            () => new ApprovalAuthorityQuery(Org, null!, requester, approver, "publicacao-final"));
        Assert.Throws<ArgumentNullException>(
            () => new ApprovalAuthorityQuery(Org, Policy, null!, approver, "publicacao-final"));
        Assert.Throws<ArgumentNullException>(
            () => new ApprovalAuthorityQuery(Org, Policy, requester, null!, "publicacao-final"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" publicacao-final")]
    public void Query_rejects_non_structural_action(string? action)
    {
        var approver = new PositionEndpointRef(PositionId.From("delivery-lead"));

        Assert.ThrowsAny<ArgumentException>(
            () => new ApprovalAuthorityQuery(
                Org,
                Policy,
                PositionId.From("engineer"),
                approver,
                action!));
    }
}
