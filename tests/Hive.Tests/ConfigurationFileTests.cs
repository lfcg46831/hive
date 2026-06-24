using System.Text.Json;

namespace Hive.Tests;

public sealed class ConfigurationFileTests
{
    public static TheoryData<string, string[]> BaseRoleDefaults => new()
    {
        { "src/Hive.Api/appsettings.json", new[] { "api" } },
        { "src/Hive.Worker/appsettings.json", new[] { "agents", "gateway", "connectors" } },
    };

    [Theory]
    [MemberData(nameof(BaseRoleDefaults))]
    public void Base_configuration_declares_expected_roles(string relativePath, string[] expectedRoles)
    {
        using var document = Load(relativePath);

        var roles = document.RootElement
            .GetProperty("Hive")
            .GetProperty("Node")
            .GetProperty("Roles")
            .EnumerateArray()
            .Select(role => role.GetString())
            .ToArray();

        Assert.Equal(expectedRoles, roles);
    }

    [Theory]
    [InlineData("src/Hive.Api/appsettings.json")]
    [InlineData("src/Hive.Worker/appsettings.json")]
    public void Base_configuration_declares_empty_PostgreSql_contract(string relativePath)
    {
        using var document = Load(relativePath);

        var connectionString = document.RootElement
            .GetProperty("ConnectionStrings")
            .GetProperty("PostgreSql")
            .GetString();

        Assert.Equal(string.Empty, connectionString);
    }

    [Theory]
    [InlineData("src/Hive.Api/appsettings.json")]
    [InlineData("src/Hive.Worker/appsettings.json")]
    public void Base_configuration_declares_the_organization_root(string relativePath)
    {
        using var document = Load(relativePath);

        var rootPath = document.RootElement
            .GetProperty("Hive")
            .GetProperty("Organizations")
            .GetProperty("RootPath")
            .GetString();

        Assert.Equal("config/organizations", rootPath);
    }

    [Fact]
    public void Api_development_configuration_declares_all_in_one_roles()
    {
        using var document = Load("src/Hive.Api/appsettings.Development.json");

        var roles = document.RootElement
            .GetProperty("Hive")
            .GetProperty("Node")
            .GetProperty("Roles")
            .EnumerateArray()
            .Select(role => role.GetString())
            .ToArray();

        Assert.Equal(new[] { "api", "agents", "gateway", "connectors" }, roles);
    }

    [Fact]
    public void Worker_development_configuration_inherits_base_roles()
    {
        using var document = Load("src/Hive.Worker/appsettings.Development.json");

        Assert.False(document.RootElement.TryGetProperty("Hive", out _));
    }

    private static JsonDocument Load(string relativePath)
    {
        var path = Path.Combine(RepositoryRoot, relativePath);
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
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
