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

    [Fact]
    public void T17f_compose_profile_enforces_hybrid_resolution_without_committing_a_secret()
    {
        var profile = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docker-compose.evaluation.outcome-resolution.yml"));
        var organization = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "hybrid-outcome-resolution-v1",
            "organization.yaml"));

        Assert.Contains("HIVE__OUTCOMES__MODE: \"enforcement\"", profile, StringComparison.Ordinal);
        Assert.Contains(
            "./config/experiments/hybrid-outcome-resolution-v1/organization.yaml",
            profile,
            StringComparison.Ordinal);
        Assert.Contains("identity_prompt_ref: triage-v2", organization, StringComparison.Ordinal);
        Assert.Contains("model: gpt-5-mini-2025-08-07", organization, StringComparison.Ordinal);
        Assert.Contains("timeout: PT45S", organization, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", profile, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", organization, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T18a_timeout60_profile_is_isolated_from_the_historical_T17f_profile()
    {
        var historicalProfile = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docker-compose.evaluation.outcome-resolution.yml"));
        var historicalOrganization = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "hybrid-outcome-resolution-v1",
            "organization.yaml"));
        var timeout60Profile = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docker-compose.evaluation.outcome-resolution-timeout60.yml"));
        var timeout60Organization = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "hybrid-outcome-resolution-timeout60-v1",
            "organization.yaml"));

        Assert.Contains("timeout: PT45S", historicalOrganization, StringComparison.Ordinal);
        Assert.Contains("timeout: PT60S", timeout60Organization, StringComparison.Ordinal);
        Assert.Contains(
            "./config/experiments/hybrid-outcome-resolution-v1/organization.yaml",
            historicalProfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "./config/experiments/hybrid-outcome-resolution-timeout60-v1/organization.yaml",
            timeout60Profile,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE__OUTCOMES__MODE: \"enforcement\"",
            timeout60Profile,
            StringComparison.Ordinal);
        Assert.Contains(
            "identity_prompt_ref: triage-v2",
            timeout60Organization,
            StringComparison.Ordinal);
        Assert.Contains(
            "model: gpt-5-mini-2025-08-07",
            timeout60Organization,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", timeout60Profile, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", timeout60Profile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", timeout60Organization, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T18e_reliability_profile_is_isolated_from_historical_profiles()
    {
        var timeout60Organization = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "hybrid-outcome-resolution-timeout60-v1",
            "organization.yaml"));
        var reliabilityProfile = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docker-compose.evaluation.outcome-resolution-reliability.yml"));
        var reliabilityOrganization = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "hybrid-outcome-resolution-reliability-v1",
            "organization.yaml"));

        Assert.Contains("max_tokens: 4096", timeout60Organization, StringComparison.Ordinal);
        Assert.Contains("timeout: PT60S", reliabilityOrganization, StringComparison.Ordinal);
        Assert.Contains("max_tokens: 8192", reliabilityOrganization, StringComparison.Ordinal);
        Assert.Contains(
            "./config/experiments/hybrid-outcome-resolution-reliability-v1/organization.yaml",
            reliabilityProfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE__OUTCOMES__MODE: \"enforcement\"",
            reliabilityProfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "identity_prompt_ref: triage-v2",
            reliabilityOrganization,
            StringComparison.Ordinal);
        Assert.Contains(
            "model: gpt-5-mini-2025-08-07",
            reliabilityOrganization,
            StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", reliabilityProfile, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", reliabilityProfile, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("sk-", reliabilityOrganization, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void T18j_verifier30_profile_changes_only_the_systemic_verifier_deadline()
    {
        var reliabilityProfile = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docker-compose.evaluation.outcome-resolution-reliability.yml"));
        var verifier30Profile = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "docker-compose.evaluation.outcome-resolution-verifier30.yml"));
        var reliabilityOrganization = File.ReadAllText(Path.Combine(
            RepositoryRoot,
            "config",
            "experiments",
            "hybrid-outcome-resolution-reliability-v1",
            "organization.yaml"));

        Assert.DoesNotContain(
            "HIVE__OUTCOMES__VERIFIERTIMEOUT",
            reliabilityProfile,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE__OUTCOMES__VERIFIERTIMEOUT: \"00:00:30\"",
            verifier30Profile,
            StringComparison.Ordinal);
        Assert.Contains(
            "HIVE__OUTCOMES__MODE: \"enforcement\"",
            verifier30Profile,
            StringComparison.Ordinal);
        Assert.Contains(
            "./config/experiments/hybrid-outcome-resolution-reliability-v1/organization.yaml",
            verifier30Profile,
            StringComparison.Ordinal);
        Assert.Contains("timeout: PT60S", reliabilityOrganization, StringComparison.Ordinal);
        Assert.Contains("max_tokens: 8192", reliabilityOrganization, StringComparison.Ordinal);
        Assert.DoesNotContain("OPENAI_API_KEY", verifier30Profile, StringComparison.Ordinal);
        Assert.DoesNotContain("sk-", verifier30Profile, StringComparison.OrdinalIgnoreCase);
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
