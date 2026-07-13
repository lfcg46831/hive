namespace Hive.Tests;

public sealed class ComposeDemoConfigurationTests
{
    [Fact]
    public void Demo_compose_override_enables_real_provider_without_committing_a_secret()
    {
        var singleNode = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.demo.yml"));
        var cluster = File.ReadAllText(Path.Combine(RepositoryRoot, "docker-compose.demo.cluster.yml"));
        var environmentTemplate = File.ReadAllText(Path.Combine(RepositoryRoot, ".env.example"));

        Assert.Contains("HIVE__NODE__ROLES__0: \"api\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__NODE__ROLES__1: \"agents\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__NODE__ROLES__2: \"gateway\"", singleNode, StringComparison.Ordinal);
        Assert.Contains("HIVE__NODE__ROLES__3: \"connectors\"", singleNode, StringComparison.Ordinal);
        AssertRealProviderProfile(singleNode);
        Assert.Contains("api2:", cluster, StringComparison.Ordinal);
        Assert.Contains("api3:", cluster, StringComparison.Ordinal);
        AssertRealProviderProfile(cluster);
        Assert.DoesNotContain("sk-", singleNode, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", cluster, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("OPENAI_API_KEY=", environmentTemplate, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY=sk-", environmentTemplate, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertRealProviderProfile(string text)
    {
        Assert.Contains("HIVE__AIGATEWAY__PROVIDER: \"real\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PROVIDERID: \"openai\"", text, StringComparison.Ordinal);
        Assert.Contains("${OPENAI_MODEL_ID:-gpt-5-mini}", text, StringComparison.Ordinal);
        Assert.Contains("${OPENAI_API_KEY:?", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__OUTPUTCAPABILITIES__0: \"json-schema\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__OUTPUTCAPABILITIES__1: \"json-object\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__OUTPUTCAPABILITIES__2: \"text\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PRICING__VERSION: \"openai-2026-07-13\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PRICING__TOKENUNIT: \"1000000\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__MODELID: \"gpt-5-mini\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__INPUTPRICE: \"0.25\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__OUTPUTPRICE: \"2.00\"", text, StringComparison.Ordinal);
        Assert.Contains("HIVE__AIGATEWAY__REAL__PRICING__MODELS__0__CURRENCY: \"USD\"", text, StringComparison.Ordinal);
        Assert.DoesNotContain("HIVE__AIGATEWAY__STUB__SCENARIO", text, StringComparison.Ordinal);
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
