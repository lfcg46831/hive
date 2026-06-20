using Hive.Infrastructure.Configuration;

namespace Hive.Tests;

public sealed class HiveOptionsValidatorTests
{
    private readonly HiveOptionsValidator _validator = new();

    [Fact]
    public void Canonical_roles_are_valid()
    {
        var result = Validate(["agents", "gateway", "connectors", "api"]);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public void Trimmed_case_insensitive_roles_are_valid_without_mutation()
    {
        var roles = new[] { " API ", "Agents" };
        var options = CreateOptions(roles);

        var result = _validator.Validate(null, options);

        Assert.True(result.Succeeded);
        Assert.Same(roles, options.Node.Roles);
        Assert.Equal(new[] { " API ", "Agents" }, options.Node.Roles);
    }

    [Fact]
    public void Empty_role_list_is_invalid()
    {
        var result = Validate([]);

        AssertFailure(result, "Hive:Node:Roles must contain at least one role.");
    }

    [Fact]
    public void Null_role_list_is_invalid()
    {
        var options = new HiveOptions();
        options.Node.Roles = null!;

        var result = _validator.Validate(null, options);

        AssertFailure(result, "Hive:Node:Roles must contain at least one role.");
    }

    [Fact]
    public void Empty_unknown_and_duplicate_values_are_reported_together()
    {
        var result = Validate(["api", " API ", "scheduler", " ", null!]);
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures).ToArray();

        Assert.True(result.Failed);
        Assert.Contains(failures, failure => failure.Contains("Hive:Node:Roles[2]") && failure.Contains("\"scheduler\""));
        Assert.Contains(failures, failure => failure.Contains("Hive:Node:Roles[3]") && failure.Contains("must not be empty"));
        Assert.Contains(failures, failure => failure.Contains("Hive:Node:Roles[4]") && failure.Contains("<null>"));
        Assert.Contains(failures, failure => failure.Contains("duplicate role values") && failure.Contains("\"api\"") && failure.Contains("\" API \""));
    }

    [Theory]
    [InlineData("api", "api")]
    [InlineData("api", "API")]
    [InlineData("api", " API ")]
    public void Duplicate_roles_use_trimmed_case_insensitive_comparison(string first, string second)
    {
        var result = Validate([first, second]);
        var failures = Assert.IsAssignableFrom<IEnumerable<string>>(result.Failures);

        Assert.Contains(
            failures,
            failure => failure.Contains("Hive:Node:Roles") && failure.Contains("duplicate role values"));
    }

    private Microsoft.Extensions.Options.ValidateOptionsResult Validate(string[] roles) =>
        _validator.Validate(null, CreateOptions(roles));

    private static HiveOptions CreateOptions(string[] roles) =>
        new()
        {
            Node = new NodeOptions
            {
                Roles = roles,
            },
        };

    private static void AssertFailure(
        Microsoft.Extensions.Options.ValidateOptionsResult result,
        string expectedFailure)
    {
        Assert.True(result.Failed);
        Assert.Contains(expectedFailure, result.Failures);
    }
}
