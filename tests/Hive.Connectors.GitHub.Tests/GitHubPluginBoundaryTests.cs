using System.Xml.Linq;

namespace Hive.Connectors.GitHub.Tests;

public sealed class GitHubPluginBoundaryTests
{
    [Fact]
    public void Plugin_depends_on_generic_core_but_main_solution_does_not_depend_on_plugin()
    {
        var project = XDocument.Load(Path.Combine(
            RepositoryRoot,
            "src",
            "Hive.Connectors.GitHub",
            "Hive.Connectors.GitHub.csproj"));
        var references = project
            .Descendants("ProjectReference")
            .Select(reference => reference.Attribute("Include")?.Value.Replace('\\', '/'))
            .OfType<string>()
            .ToArray();
        var mainSolution = File.ReadAllText(Path.Combine(RepositoryRoot, "Hive.sln"));

        Assert.Equal(
            [
                "../Hive.Domain/Hive.Domain.csproj",
                "../Hive.Infrastructure/Hive.Infrastructure.csproj",
            ],
            references);
        Assert.DoesNotContain("Hive.Connectors.GitHub", mainSolution, StringComparison.Ordinal);
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
