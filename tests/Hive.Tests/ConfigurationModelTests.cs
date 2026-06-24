using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

public sealed class ConfigurationModelTests
{
    [Fact]
    public void Configuration_contract_uses_canonical_section_names()
    {
        Assert.Equal("Hive", HiveOptions.SectionName);
        Assert.Equal("PostgreSql", ConnectionStringNames.PostgreSql);
    }

    [Fact]
    public void Node_roles_match_the_canonical_F0_values()
    {
        var expected = new[] { "agents", "gateway", "connectors", "api" };

        Assert.Equal(expected, NodeRoleNames.All);
        Assert.Equal(expected.Length, NodeRoleNames.All.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal("agents", NodeRoleNames.Agents);
        Assert.Equal("gateway", NodeRoleNames.Gateway);
        Assert.Equal("connectors", NodeRoleNames.Connectors);
        Assert.Equal("api", NodeRoleNames.Api);
    }

    [Fact]
    public void Options_have_non_null_empty_defaults()
    {
        var options = new HiveOptions();

        Assert.NotNull(options.Node);
        Assert.NotNull(options.Node.Roles);
        Assert.Empty(options.Node.Roles);
        Assert.NotNull(options.Organizations);
        Assert.Equal(Path.Combine("config", "organizations"), options.Organizations.RootPath);
    }
}
