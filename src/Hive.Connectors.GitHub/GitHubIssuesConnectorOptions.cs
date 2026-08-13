namespace Hive.Connectors.GitHub;

/// <summary>
/// Raw .NET configuration binding for GitHub Issues connector instances and their separately
/// supplied operational credentials. The validated, credential-free projection is exposed through
/// <see cref="GitHubIssuesConnectorConfigurationCatalog"/>.
/// </summary>
internal sealed class GitHubIssuesConnectorOptions
{
    public const string SectionName = "Hive:Connectors:GitHubIssues";

    public GitHubIssuesConnectorInstanceOptions[]? Instances { get; set; }

    public GitHubIssuesConnectorCredentialOptions[]? Credentials { get; set; }
}

internal sealed class GitHubIssuesConnectorInstanceOptions
{
    public string? InstanceId { get; set; }

    public string? OrganizationId { get; set; }

    public string[]? Repositories { get; set; }

    public string? InboundDirectiveTarget { get; set; }

    public string[]? OutboundOperations { get; set; }

    public GitHubIssuesPollingOptions? Polling { get; set; }
}

internal sealed class GitHubIssuesPollingOptions
{
    /// <summary>ISO-8601 duration such as <c>PT30S</c>.</summary>
    public string? Interval { get; set; }

    public int? PageSize { get; set; }
}

/// <summary>Infrastructure-only secret binding. Never copy it into organization configuration.</summary>
internal sealed class GitHubIssuesConnectorCredentialOptions
{
    public string? InstanceId { get; set; }

    public string? Token { get; set; }
}
