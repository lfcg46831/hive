namespace Hive.Infrastructure.Configuration;

public static class NodeRoleNames
{
    public const string Agents = "agents";
    public const string Gateway = "gateway";
    public const string Connectors = "connectors";
    public const string Api = "api";

    public static IReadOnlyList<string> All { get; } =
        Array.AsReadOnly(new[] { Agents, Gateway, Connectors, Api });
}
