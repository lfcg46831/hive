using System.Xml.Linq;

namespace Hive.Tests;

public sealed class SolutionStructureTests
{
    public static TheoryData<string, string, string> RequiredProjects => new()
    {
        { "Hive.Domain", "src/Hive.Domain/Hive.Domain.csproj", "Microsoft.NET.Sdk" },
        { "Hive.Actors", "src/Hive.Actors/Hive.Actors.csproj", "Microsoft.NET.Sdk" },
        { "Hive.Infrastructure", "src/Hive.Infrastructure/Hive.Infrastructure.csproj", "Microsoft.NET.Sdk" },
        { "Hive.Api", "src/Hive.Api/Hive.Api.csproj", "Microsoft.NET.Sdk.Web" },
        { "Hive.Worker", "src/Hive.Worker/Hive.Worker.csproj", "Microsoft.NET.Sdk.Worker" },
        { "Hive.Tests", "tests/Hive.Tests/Hive.Tests.csproj", "Microsoft.NET.Sdk" },
    };

    [Theory]
    [MemberData(nameof(RequiredProjects))]
    public void Required_project_exists_and_targets_net8(string projectName, string relativePath, string expectedSdk)
    {
        var projectPath = Path.Combine(RepositoryRoot, relativePath);

        Assert.True(File.Exists(projectPath), $"{projectName} project is missing at {relativePath}.");

        var project = XDocument.Load(projectPath);

        Assert.Equal(expectedSdk, project.Root?.Attribute("Sdk")?.Value);
        Assert.Equal("net8.0", project.Root?.Descendants("TargetFramework").SingleOrDefault()?.Value);
    }

    [Theory]
    [MemberData(nameof(RequiredProjects))]
    public void Required_project_is_registered_in_solution(string projectName, string relativePath, string _)
    {
        var solutionText = File.ReadAllText(Path.Combine(RepositoryRoot, "Hive.sln"));
        var solutionPath = relativePath.Replace('/', '\\');

        Assert.Contains($"= \"{projectName}\", \"{solutionPath}\"", solutionText);
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
