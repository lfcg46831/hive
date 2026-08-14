using Hive.Domain.Identity;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubIssuesScopePolicyTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public void Inbound_scope_is_case_insensitive_and_denies_unknown_or_malformed_repositories()
    {
        var instance = Instance();

        var exact = GitHubIssuesScopePolicy.AuthorizeInbound(instance, "acme/payments");
        var differentCase = GitHubIssuesScopePolicy.AuthorizeInbound(instance, "ACME/PAYMENTS");
        var unknown = GitHubIssuesScopePolicy.AuthorizeInbound(instance, "other/private");
        var malformed = GitHubIssuesScopePolicy.AuthorizeInbound(instance, "not-a-repository");

        Assert.True(exact.IsAllowed);
        Assert.Null(exact.DeniedDimension);
        Assert.True(differentCase.IsAllowed);
        Assert.False(unknown.IsAllowed);
        Assert.Equal(GitHubIssuesScopeDimensions.Repository, unknown.DeniedDimension);
        Assert.False(malformed.IsAllowed);
        Assert.Equal(GitHubIssuesScopeDimensions.Repository, malformed.DeniedDimension);
        Assert.Equal("acme/payments", GitHubIssuesScopePolicy.CanonicalRepository("Acme/Payments"));
        Assert.Equal("invalid", GitHubIssuesScopePolicy.CanonicalRepository("not-a-repository"));
    }

    [Fact]
    public void Outbound_scope_allows_only_the_matching_instance_organization_repository_and_operation()
    {
        var instance = Instance();

        AssertAllowed(GitHubIssuesScopePolicy.AuthorizeOutbound(
            instance,
            Issue(),
            GitHubIssuesOutboundOperations.Comment));
        AssertDenied(
            GitHubIssuesScopePolicy.AuthorizeOutbound(
                instance,
                Issue(instanceId: "other-github"),
                GitHubIssuesOutboundOperations.Comment),
            GitHubIssuesScopeDimensions.Instance);
        AssertDenied(
            GitHubIssuesScopePolicy.AuthorizeOutbound(
                instance,
                Issue(organizationId: OrganizationId.From("other")),
                GitHubIssuesOutboundOperations.Comment),
            GitHubIssuesScopeDimensions.Instance);
        AssertDenied(
            GitHubIssuesScopePolicy.AuthorizeOutbound(
                instance,
                Issue(repository: "other/private"),
                GitHubIssuesOutboundOperations.Comment),
            GitHubIssuesScopeDimensions.Repository);
        AssertDenied(
            GitHubIssuesScopePolicy.AuthorizeOutbound(
                instance,
                Issue(),
                GitHubIssuesOutboundOperations.UpdateState),
            GitHubIssuesScopeDimensions.Operation);
    }

    private static void AssertAllowed(GitHubIssuesScopeDecision decision)
    {
        Assert.True(decision.IsAllowed);
        Assert.Null(decision.DeniedDimension);
    }

    private static void AssertDenied(
        GitHubIssuesScopeDecision decision,
        string dimension)
    {
        Assert.False(decision.IsAllowed);
        Assert.Equal(dimension, decision.DeniedDimension);
    }

    private static GitHubIssuesConnectorInstanceConfiguration Instance() =>
        new(
            "acme-github",
            Organization,
            ["Acme/Payments"],
            PositionId.From("bug-triage"),
            [GitHubIssuesOutboundOperations.Comment],
            new GitHubIssuesPollingConfiguration(TimeSpan.FromSeconds(30), 100));

    private static GitHubIssueCorrelation Issue(
        string instanceId = "acme-github",
        OrganizationId? organizationId = null,
        string repository = "acme/payments") =>
        new(
            instanceId,
            organizationId ?? Organization,
            repository,
            42,
            Thread,
            Directive);
}
