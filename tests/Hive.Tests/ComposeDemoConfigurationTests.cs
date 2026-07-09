namespace Hive.Tests;

public sealed class ComposeDemoConfigurationTests
{
    [Fact]
    public void Demo_compose_override_enables_reproducible_vertical_slice_defaults()
    {
        var singleNode = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.demo.yml"));
        var cluster = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.demo.cluster.yml"));

        Assert.Contains("HIVE__NODE__ROLES__0: \"api\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__NODE__ROLES__1: \"agents\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__NODE__ROLES__2: \"gateway\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__NODE__ROLES__3: \"connectors\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__PROVIDER: \"stub\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__STUB__SCENARIO: \"bug-triage-report\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("api2:", cluster, StringComparison.Ordinal);
        Assert.Contains("api3:", cluster, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__PROVIDER: \"stub\"", cluster, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__STUB__SCENARIO: \"bug-triage-report\"", cluster, StringComparison.Ordinal);
    }

    [Fact]
    public void Configuration_documents_demo_commands_for_one_and_three_node_topologies()
    {
        var text = File.ReadAllText(Path.Combine(RepositoryRoot, "docs", "configuration.md"));

        Assert.Contains("docker-compose.demo.yml", text, StringComparison.Ordinal);
        Assert.Contains("docker-compose.demo.cluster.yml", text, StringComparison.Ordinal);
        Assert.Contains(
            "dotnet run --project src/Hive.DemoClient -- --submit",
            text,
            StringComparison.Ordinal);
        Assert.Contains("--seed us-f0-10-t12-demo", text, StringComparison.Ordinal);
        Assert.Contains(
            "docker compose -f docker-compose.yml -f docker-compose.cluster.yml -f docker-compose.roles.yml -f docker-compose.demo.cluster.yml up --build",
            text,
            StringComparison.Ordinal);
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }
}
