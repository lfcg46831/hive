using Hive.Domain.Connectors;

namespace Hive.Connectors.GitHub;

internal static class GitHubIssuesScopeDimensions
{
    public const string Instance = "instance";
    public const string Repository = "repository";
    public const string Operation = "operation";
}

internal readonly record struct GitHubIssuesScopeDecision
{
    private GitHubIssuesScopeDecision(bool isAllowed, string? deniedDimension)
    {
        IsAllowed = isAllowed;
        DeniedDimension = deniedDimension;
    }

    public bool IsAllowed { get; }

    public string? DeniedDimension { get; }

    public static GitHubIssuesScopeDecision Allowed() => new(true, deniedDimension: null);

    public static GitHubIssuesScopeDecision Denied(string dimension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dimension);
        return new(false, dimension);
    }
}

internal sealed class GitHubIssuesScopeDeniedException : Exception
{
    public GitHubIssuesScopeDeniedException()
        : base("The GitHub Issues operation is outside the configured connector scope.")
    {
    }

    public string ErrorCode => GitHubIssuesScopePolicy.ScopeDeniedCode;
}

/// <summary>
/// Pure, credential-free scope policy shared by the polling and tool-execution boundaries. The
/// immutable connector-instance configuration is the only authority source.
/// </summary>
internal static class GitHubIssuesScopePolicy
{
    public static string ScopeDeniedCode { get; } =
        ConnectorErrorCodeContract.ToWireValue(ConnectorErrorCode.ScopeDenied);

    public static GitHubIssuesScopeDecision AuthorizeInbound(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string repository)
    {
        ArgumentNullException.ThrowIfNull(instance);
        return ContainsRepository(instance, repository)
            ? GitHubIssuesScopeDecision.Allowed()
            : GitHubIssuesScopeDecision.Denied(GitHubIssuesScopeDimensions.Repository);
    }

    public static GitHubIssuesScopeDecision AuthorizeOutbound(
        GitHubIssuesConnectorInstanceConfiguration instance,
        GitHubIssueCorrelation issue,
        string operation)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(operation);

        if (!string.Equals(instance.InstanceId, issue.InstanceId, StringComparison.Ordinal)
            || instance.OrganizationId != issue.OrganizationId)
        {
            return GitHubIssuesScopeDecision.Denied(GitHubIssuesScopeDimensions.Instance);
        }

        if (!ContainsRepository(instance, issue.Repository))
        {
            return GitHubIssuesScopeDecision.Denied(GitHubIssuesScopeDimensions.Repository);
        }

        return instance.OutboundOperations.Contains(operation, StringComparer.Ordinal)
            ? GitHubIssuesScopeDecision.Allowed()
            : GitHubIssuesScopeDecision.Denied(GitHubIssuesScopeDimensions.Operation);
    }

    public static string CanonicalRepository(string? repository) =>
        GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository)
            ? repository!.ToLowerInvariant()
            : "invalid";

    private static bool ContainsRepository(
        GitHubIssuesConnectorInstanceConfiguration instance,
        string? repository) =>
        GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository)
        && instance.Repositories.Contains(repository!, StringComparer.OrdinalIgnoreCase);
}
