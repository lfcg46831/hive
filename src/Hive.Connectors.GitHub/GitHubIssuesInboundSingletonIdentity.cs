namespace Hive.Connectors.GitHub;

internal static class GitHubIssuesInboundSingletonIdentity
{
    public const string SingletonManagerName = "github-issues-inbound-singleton-manager";
    public const string SingletonName = "github-issues-inbound-source";
    public const string ProxyName = "github-issues-inbound-singleton";
    public const string SingletonManagerPath = "/user/" + SingletonManagerName;
}
