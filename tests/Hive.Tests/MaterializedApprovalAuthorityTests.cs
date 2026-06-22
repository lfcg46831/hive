using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

public sealed class MaterializedApprovalAuthorityTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly ApprovalPolicyRef FinalPublication =
        ApprovalPolicyRef.From("requires-human-approval");
    private static readonly ApprovalPolicyVersion Version =
        ApprovalPolicyVersion.Create("v3", "sha256:abc123");

    [Fact]
    public async Task Resolves_authorized_position_approver_with_applied_version()
    {
        var authority = SampleAuthority();

        var resolution = await authority.ResolveApproverAsync(
            Query("publicacao-final", Position("engineer")));

        Assert.Equal(ApproverResolutionStatus.Resolved, resolution.Status);
        Assert.True(resolution.IsResolved);
        Assert.Equal(new PositionEndpointRef(PositionId.From("delivery-lead")), resolution.ResolvedApprover);
        Assert.Equal(Version, resolution.AppliedVersion);
        Assert.Equal(FinalPublication, resolution.Policy);
    }

    [Fact]
    public async Task Resolves_owner_as_approver_when_policy_points_to_owner()
    {
        var authority = new MaterializedApprovalAuthority(
            ApprovalAuthoritySnapshot
                .CreateBuilder(Org)
                .AddPolicy(
                    ApprovalPolicyRef.From("budget-commitment"),
                    new OrganizationOwnerEndpointRef(),
                    Version,
                    ["compromissos-orcamentais"])
                .Build());

        var resolution = await authority.ResolveApproverAsync(
            new ApprovalAuthorityQuery(
                Org,
                ApprovalPolicyRef.From("budget-commitment"),
                PositionId.From("engineer"),
                new OrganizationOwnerEndpointRef(),
                "compromissos-orcamentais"));

        Assert.Equal(ApproverResolutionStatus.Resolved, resolution.Status);
        Assert.Equal(new OrganizationOwnerEndpointRef(), resolution.ResolvedApprover);
        Assert.Equal(Version, resolution.AppliedVersion);
    }

    [Fact]
    public async Task Unknown_policy_is_reported_without_approver_or_version()
    {
        var authority = SampleAuthority();

        var resolution = await authority.ResolveApproverAsync(
            new ApprovalAuthorityQuery(
                Org,
                ApprovalPolicyRef.From("ghost-policy"),
                PositionId.From("engineer"),
                Position("delivery-lead"),
                "publicacao-final"));

        Assert.Equal(ApproverResolutionStatus.PolicyNotFound, resolution.Status);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.ResolvedApprover);
        Assert.Null(resolution.AppliedVersion);
        Assert.Equal(ApprovalPolicyRef.From("ghost-policy"), resolution.Policy);
    }

    [Fact]
    public async Task Action_outside_the_policy_keeps_the_applied_version_for_audit()
    {
        var authority = SampleAuthority();

        var resolution = await authority.ResolveApproverAsync(
            Query("comunicacao-externa-oficial", Position("engineer")));

        Assert.Equal(ApproverResolutionStatus.ActionNotAuthorized, resolution.Status);
        Assert.False(resolution.IsResolved);
        Assert.Null(resolution.ResolvedApprover);
        Assert.Equal(Version, resolution.AppliedVersion);
    }

    [Fact]
    public async Task Unknown_organization_is_a_structural_failure()
    {
        var authority = SampleAuthority();
        var query = new ApprovalAuthorityQuery(
            OrganizationId.From("unknown-organization"),
            FinalPublication,
            PositionId.From("engineer"),
            Position("delivery-lead"),
            "publicacao-final");

        await Assert.ThrowsAsync<ApprovalAuthorityNotFoundException>(
            async () => await authority.ResolveApproverAsync(query));
    }

    [Fact]
    public async Task Cancellation_propagates_before_resolution()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var authority = SampleAuthority();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await authority.ResolveApproverAsync(
                Query("publicacao-final", Position("engineer")),
                cancellation.Token));
    }

    [Fact]
    public async Task Missing_query_is_api_misuse()
    {
        var authority = SampleAuthority();

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await authority.ResolveApproverAsync(null!));
    }

    [Fact]
    public void Missing_snapshot_dependency_is_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MaterializedApprovalAuthority((ApprovalAuthoritySnapshot)null!));
    }

    [Fact]
    public void Duplicate_organization_snapshots_are_rejected()
    {
        var first = ApprovalAuthoritySnapshot.CreateBuilder(Org)
            .AddPolicy(FinalPublication, Position("delivery-lead"), Version, ["publicacao-final"])
            .Build();
        var second = ApprovalAuthoritySnapshot.CreateBuilder(Org)
            .AddPolicy(FinalPublication, Position("ceo"), Version, ["publicacao-final"])
            .Build();

        Assert.Throws<ArgumentException>(
            () => new MaterializedApprovalAuthority([first, second]));
    }

    [Fact]
    public void Duplicate_policy_in_a_snapshot_is_rejected()
    {
        var builder = ApprovalAuthoritySnapshot.CreateBuilder(Org)
            .AddPolicy(FinalPublication, Position("delivery-lead"), Version, ["publicacao-final"]);

        Assert.Throws<ArgumentException>(
            () => builder.AddPolicy(FinalPublication, Position("ceo"), Version, ["publicacao-final"]));
    }

    [Fact]
    public void A_policy_without_actions_is_rejected()
    {
        var builder = ApprovalAuthoritySnapshot.CreateBuilder(Org);

        Assert.Throws<ArgumentException>(
            () => builder.AddPolicy(FinalPublication, Position("delivery-lead"), Version, []));
    }

    [Fact]
    public void A_policy_authorizing_a_system_endpoint_is_rejected()
    {
        var builder = ApprovalAuthoritySnapshot.CreateBuilder(Org);

        Assert.Throws<ArgumentException>(
            () => builder.AddPolicy(
                FinalPublication,
                new SystemEndpointRef(SystemEndpointKind.Scheduler),
                Version,
                ["publicacao-final"]));
    }

    private static ApprovalAuthorityQuery Query(string action, EndpointRef proposedApprover) =>
        new(Org, FinalPublication, PositionId.From("engineer"), proposedApprover, action);

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static MaterializedApprovalAuthority SampleAuthority() =>
        new(ApprovalAuthoritySnapshot
            .CreateBuilder(Org)
            .AddPolicy(
                FinalPublication,
                Position("delivery-lead"),
                Version,
                ["publicacao-final"])
            .Build());
}
