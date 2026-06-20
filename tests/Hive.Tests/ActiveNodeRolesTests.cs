using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class ActiveNodeRolesTests
{
    [Fact]
    public void Trims_and_canonicalizes_declared_roles_to_lowercase()
    {
        var roles = Create(" API ", "Agents");

        Assert.Equal(new[] { "agents", "api" }, roles.Values.OrderBy(value => value).ToArray());
    }

    [Fact]
    public void Membership_is_case_insensitive_and_role_aware()
    {
        var roles = Create(NodeRoleNames.Connectors);

        Assert.True(roles.Contains("CONNECTORS"));
        Assert.False(roles.Contains(NodeRoleNames.Api));
    }

    [Fact]
    public void Ignores_whitespace_only_entries()
    {
        var roles = Create(NodeRoleNames.Api, "   ");

        Assert.Equal(new[] { "api" }, roles.Values.ToArray());
    }

    private static ActiveNodeRoles Create(params string[] roles) =>
        new(Options.Create(new HiveOptions { Node = new NodeOptions { Roles = roles } }));
}
